using System.Diagnostics;
using AudioCore.Impl;

namespace AudioCore_Tests;

[TestClass]
public sealed class FfmpegAudioReader_Tests
{
    private string _inputPath = null!;

    [TestInitialize]
    public void Init()
    {
        var baseDir = AppContext.BaseDirectory;
        _inputPath = Path.Combine(baseDir, "Data", "test_input.mp3");

        Assert.IsTrue(File.Exists(_inputPath), "Test input audio missing");
    }

    [TestMethod]
    public void Reader_Opens_And_Reports_Properties()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        Assert.AreEqual(44100, reader.SampleRate);
        Assert.AreEqual(2, reader.Channels);
        Assert.IsGreaterThan(0, reader.TotalSamples);
    }

    [TestMethod]
    public async Task Reader_Reads_Some_Samples()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf = new float[44100];
        var read = await reader.ReadAsync(buf.AsMemory(), CancellationToken.None);

        Assert.IsGreaterThan(0, read, "Reader returned no samples");
        Assert.IsLessThanOrEqualTo(buf.Length, read);
    }

    [TestMethod]
    public async Task Reader_Seek_Works()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf1 = new float[44100];
        var buf2 = new float[44100];

        var r1 = await reader.ReadAsync(buf1.AsMemory(), CancellationToken.None);
        Assert.IsGreaterThan(0, r1);

        reader.Seek(reader.SampleRate*5);

        var r2 = await reader.ReadAsync(buf2.AsMemory(), CancellationToken.None);
        Assert.IsGreaterThan(0, r2);

        bool identical = true;
        for (var i = 0; i < Math.Min(r1, r2); i++)
        {
            if (buf1[i] != buf2[i])
            {
                identical = false;
                break;
            }
        }

        Assert.IsFalse(identical, "Seek did not change decoded samples");
    }

    [TestMethod]
    public async Task Reader_Reset_Works()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf1 = new float[44100];
        var buf2 = new float[44100];

        var r1 = await reader.ReadAsync(buf1.AsMemory(), CancellationToken.None);
        Assert.IsGreaterThan(0, r1);

        reader.Reset();

        var r2 = await reader.ReadAsync(buf2.AsMemory(), CancellationToken.None);
        Assert.IsGreaterThan(0, r2);

        bool identical = true;
        for (var i = 0; i < Math.Min(r1, r2); i++)
        {
            if (buf1[i] != buf2[i])
            {
                identical = false;
                break;
            }
        }

        Assert.IsTrue(identical, "Reset did not return to start of file");
    }

    [TestMethod]
    public void Reader_Dispose_Does_Not_Throw()
    {
        var reader = new FfmpegAudioReader(_inputPath);
        reader.Dispose();
    }

    [TestMethod]
    public async Task Reader_Can_Read_Flac_File()
    {
        var baseDir = AppContext.BaseDirectory;
        var flacPath = Path.Combine(baseDir, "Data", "test_input_converted.flac");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{_inputPath}\" -compression_level 12 \"{flacPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using (var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg process"))
            {
                _ = Task.Run(() => DrainStderr(p));
                p.WaitForExit();
                Assert.AreEqual(0, p.ExitCode, "FFmpeg failed to convert MP3 to FLAC");
            }

            Assert.IsTrue(File.Exists(flacPath), "FLAC file was not created");

            using var reader = new FfmpegAudioReader(flacPath);

            Assert.AreEqual(44100, reader.SampleRate);
            Assert.AreEqual(2, reader.Channels);
            Assert.IsGreaterThan(0, reader.TotalSamples);

            var buf = new float[44100];
            var read = await reader.ReadAsync(buf.AsMemory(), CancellationToken.None);

            Assert.IsGreaterThan(0, read);
            Assert.IsLessThanOrEqualTo(buf.Length, read);

            reader.Seek(reader.SampleRate);

            var buf2 = new float[44100];
            var read2 = await reader.ReadAsync(buf2.AsMemory(), CancellationToken.None);

            Assert.IsGreaterThan(0, read2);

            bool identical = true;
            for (var i = 0; i < Math.Min(read, read2); i++)
            {
                if (buf[i] != buf2[i])
                {
                    identical = false;
                    break;
                }
            }

            Assert.IsFalse(identical, "Seek did not change decoded FLAC samples");

            reader.Reset();

            var buf3 = new float[44100];
            var read3 = await reader.ReadAsync(buf3.AsMemory(), CancellationToken.None);

            Assert.IsGreaterThan(0, read3);

            bool matchAfterReset = true;
            for (var i = 0; i < Math.Min(read, read3); i++)
            {
                if (buf[i] != buf3[i])
                {
                    matchAfterReset = false;
                    break;
                }
            }

            Assert.IsTrue(matchAfterReset, "Reset did not return to start of FLAC file");
        }
        finally
        {
            try
            {
                if (File.Exists(flacPath))
                    File.Delete(flacPath);
            }
            catch { }
        }
    }

    private static void DrainStderr(Process proc)
    {
        try
        {
            var reader = proc.StandardError;
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
