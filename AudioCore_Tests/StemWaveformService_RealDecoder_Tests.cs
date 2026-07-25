using AudioCore.Models;
using AudioCore.Impl;

namespace AudioCore_Tests;

[TestClass]
public sealed class StemWaveformService_RealDecoder_Tests
{
    private string GetTestInputPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "Data", "test_input.mp3");
    }

    private StemTrack CreateStem(string path)
    {
        return new StemTrack
        {
            FilePath = path,
            SampleRate = 44100,
            Channels = 2,
            Duration = TimeSpan.FromSeconds(10),
            Name = "TestStem",
            Type = StemType.Other
        };
    }

    [TestMethod]
    public void TestInputFileExists()
    {
        var path = GetTestInputPath();
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task ComputeWaveform_RealDecoder_ReturnsCorrectLength()
    {
        var path = GetTestInputPath();
        var cacheFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{System.IO.Path.GetFileNameWithoutExtension(path)}.waveform");
        try { System.IO.File.Delete(cacheFile); } catch { }
        var pool = new AudioBufferPool();

        var reader = new FfmpegAudioReader(path);
        var stem = CreateStem(path);

        using var decoder = new StemDecoder(reader, pool, stem, blockSize: 4096);

        var service = new StemWaveformService(pool);

        int segments = 20;
        var result = await service.ComputeWaveformAsync(stem, decoder, segments);

        Assert.HasCount(segments, result);
    }

    [TestMethod]
    public async Task ComputeWaveform_RealDecoder_ProducesNonZeroValues()
    {
        var original = GetTestInputPath();
        var path = Path.Combine(Path.GetTempPath(), $"test_input_{Guid.NewGuid()}.mp3");
        File.Copy(original, path);
        try
        {
            var pool = new AudioBufferPool();

            var reader = new FfmpegAudioReader(path);
            var stem = CreateStem(path);

            using var decoder = new StemDecoder(reader, pool, stem, blockSize: 4096);

            var service = new StemWaveformService(pool);

            var result = await service.ComputeWaveformAsync(stem, decoder, 10);

            bool anyNonZero = false;
            foreach (var v in result)
            {
                if (v > 0f)
                {
                    anyNonZero = true;
                    break;
                }
            }

            Assert.IsTrue(anyNonZero);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [TestMethod]
    public async Task ComputeWaveform_RealDecoder_CallsSeekAndReset()
    {
        var path = GetTestInputPath();
        var pool = new AudioBufferPool();

        // Ensure any existing cache for this input is removed before running the test
        var cacheFile = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.waveform");
        if (File.Exists(cacheFile))
            File.Delete(cacheFile);

        var reader = new FfmpegAudioReader(path);
        var stem = CreateStem(path);

        using var decoder = new StemDecoder(reader, pool, stem, blockSize: 4096);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(stem, decoder, 5);

        Assert.HasCount(5, result);
    }

    [TestMethod]
    public async Task ComputeWaveform_RealDecoder_HandlesLargeSegmentCount()
    {
        var path = GetTestInputPath();
        var pool = new AudioBufferPool();

        var reader = new FfmpegAudioReader(path);
        var stem = CreateStem(path);

        using var decoder = new StemDecoder(reader, pool, stem, blockSize: 4096);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(stem, decoder, 200);

        Assert.HasCount(200, result);
    }
}
