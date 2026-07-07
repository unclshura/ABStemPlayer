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
    public void Reader_Reads_Some_Samples()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf = new float[44100]; // 0.5 sec stereo = 22050 frames
        var read = reader.Read(buf, 0, buf.Length);

        Assert.IsGreaterThan(0, read, "Reader returned no samples");
        Assert.IsLessThanOrEqualTo(buf.Length, read);
    }

    [TestMethod]
    public void Reader_Seek_Works()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf1 = new float[44100];
        var buf2 = new float[44100];

        // Read from start
        var r1 = reader.Read(buf1, 0, buf1.Length);
        Assert.IsGreaterThan(0, r1);

        // Seek to 1 second
        reader.Seek(reader.SampleRate);

        var r2 = reader.Read(buf2, 0, buf2.Length);
        Assert.IsGreaterThan(0, r2);

        // Buffers should differ
        var identical = true;
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
    public void Reader_Reset_Works()
    {
        using var reader = new FfmpegAudioReader(_inputPath);

        var buf1 = new float[44100];
        var buf2 = new float[44100];

        var r1 = reader.Read(buf1, 0, buf1.Length);
        Assert.IsGreaterThan(0, r1);

        reader.Reset();

        var r2 = reader.Read(buf2, 0, buf2.Length);
        Assert.IsGreaterThan(0, r2);

        // After reset, buffers should match again
        var identical = true;
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
    public void Reader_Can_Read_Flac_File()
    {
        // Arrange
        var baseDir = AppContext.BaseDirectory;
        var flacPath = Path.Combine(baseDir, "Data", "test_input_converted.flac");

        try
        {
            // Convert MP3 → FLAC using FFmpeg
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
                // Start draining stderr immediately
                _ = Task.Run(() => DrainStderr(p));

                p!.WaitForExit();
                Assert.AreEqual(0, p.ExitCode, "FFmpeg failed to convert MP3 to FLAC");
            }

            Assert.IsTrue(File.Exists(flacPath), "FLAC file was not created");

            // Act
            using var reader = new FfmpegAudioReader(flacPath);

            // Assert basic properties
            Assert.AreEqual(44100, reader.SampleRate);
            Assert.AreEqual(2, reader.Channels);
            Assert.IsGreaterThan(0, reader.TotalSamples);

            // Read some samples
            var buf = new float[44100];
            var read = reader.Read(buf, 0, buf.Length);

            Assert.IsGreaterThan(0, read, "FLAC reader returned no samples");
            Assert.IsLessThanOrEqualTo(buf.Length, read);

            // Seek test
            reader.Seek(reader.SampleRate); // 1 second
            var buf2 = new float[44100];
            var read2 = reader.Read(buf2, 0, buf2.Length);

            Assert.IsGreaterThan(0, read2);

            // Buffers should differ after seek
            var identical = true;
            for (var i = 0; i < Math.Min(read, read2); i++)
            {
                if (buf[i] != buf2[i])
                {
                    identical = false;
                    break;
                }
            }

            Assert.IsFalse(identical, "Seek did not change decoded FLAC samples");

            // Reset test
            reader.Reset();
            var buf3 = new float[44100];
            var read3 = reader.Read(buf3, 0, buf3.Length);

            Assert.IsGreaterThan(0, read3);

            // After reset, buf3 should match buf
            var matchAfterReset = true;
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
            // Cleanup even if test fails
            try
            {
                if (File.Exists(flacPath))
                    File.Delete(flacPath);
            }
            catch { /* swallow */ }
        }
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
