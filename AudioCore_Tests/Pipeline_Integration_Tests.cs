using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioCore.Impl;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class Pipeline_Integration_Tests
{
    private string _inputPath = null!;
    private string _outputDir = null!;

    [TestInitialize]
    public void Init()
    {
        var baseDir = AppContext.BaseDirectory;

        _inputPath = Path.Combine(baseDir, "Data", "test_input.mp3");
        Assert.IsTrue(File.Exists(_inputPath), "Missing test input");

        _outputDir = Path.Combine(baseDir, "TestOutput", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_outputDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, true);
        }
        catch { }
    }

    [TestMethod]
    public void FullPipeline_Decoder_Mixer_Encoder_Works()
    {
        var pool = new AudioBufferPool();
        var readerFactory = new FfmpegAudioReaderFactory();
        var decoderFactory = new StemDecoderFactory(readerFactory, pool);
        var mixer = new AudioMixer(pool);

        var stems = new[]
    {
        new StemTrack { FilePath = _inputPath, Name = "stem1" },
        new StemTrack { FilePath = _inputPath, Name = "stem2" }
    };

        var decoders = stems
        .Select(s => decoderFactory.Create(s))
        .ToList();

        var settings = new MixerSettings
        {
            Stems = new[]
        {
            new StemMixSettings { Enabled = true, GainDb = 0, Pan = 0 },
            new StemMixSettings { Enabled = true, GainDb = -3, Pan = 0.2f }
        }
        };

        var outFlac = Path.Combine(_outputDir, "mixed.flac");

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
            "-y -f f32le -ar 44100 -ac 2 -i pipe:0 " +
            "-compression_level 12 " +
            $"\"{outFlac}\"",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
            WindowStyle           = ProcessWindowStyle.Hidden,
        };

        using var ff = Process.Start(psi);
        var stdin = ff!.StandardInput.BaseStream;
        // Start draining stderr immediately
        _ = Task.Run(() => DrainStderr(ff));

        var running = true;

        while (true)
        {
            var blocks = new List<AudioBlock>(decoders.Count);

            foreach (var d in decoders)
            {
                if (!d.TryDecodeNextBlock(out var block))
                {
                    foreach (var b in blocks)
                        b.Dispose();

                    running = false;
                    break;
                }

                blocks.Add(block);
            }

            if (!running)
                break;

            var mixed = mixer.Mix(blocks, settings);

            var span = mixed.Buffer.Span;
            var bytes = MemoryMarshal.AsBytes(span);
            stdin.Write(bytes);

            mixed.Dispose();
            foreach (var b in blocks)
                b.Dispose();
        }

        stdin.Close();
        ff.WaitForExit();

        Assert.AreEqual(0, ff.ExitCode, "FFmpeg failed");

        Assert.IsTrue(File.Exists(outFlac), "FLAC file was not created");
        Assert.IsGreaterThan(1024, new FileInfo(outFlac).Length, "FLAC file too small");

        using var verify = new FfmpegAudioReader(outFlac);
        var buf = new float[4096];
        var read = verify.Read(buf, 0, buf.Length);

        Assert.IsGreaterThan(0, read, "FLAC output is not decodable");
    }

    private static void DrainStderr(Process proc)
    {
        try
        {
            var reader = proc.StandardError;

            // ffmpeg writes short lines, so ReadLine is fine
            // If you want zero allocations, use ReadAsync into a rented buffer.
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                Debug.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }
}
