using AudioCore.Interfaces;
using AudioCore.Models;
using AudioCore.Impl;

namespace AudioCore_Tests;

[TestClass]
public sealed class StemWaveformService_Tests
{
    private sealed class MockDecoder : IStemDecoder
    {
        private readonly Queue<AudioBlock> _blocks;
        private readonly AudioBufferPool _pool;

        public StemTrack Stem { get; }

        public long LastSeek { get; private set; }
        public bool ResetCalled { get; private set; }

        public MockDecoder(AudioBufferPool pool, int framesPerBlock, int blocks)
        {
            _pool = pool;

            Stem = new StemTrack
            {
                Channels = 2,
                SampleRate = 44100,
                Duration = TimeSpan.FromSeconds(10),
                FilePath = "x"
            };

            _blocks = new Queue<AudioBlock>();

            long pos = 0;
            for (int i = 0; i < blocks; i++)
            {
                var buf = _pool.Rent(framesPerBlock * Stem.Channels);
                buf.Length = framesPerBlock * Stem.Channels;

                var span = buf.Span;
                for (int s = 0; s < span.Length; s++)
                {
                    span[s] = (float)(s % 100) / 100f;
                }

                _blocks.Enqueue(new AudioBlock(buf, Stem.SampleRate, Stem.Channels, pos));
                pos += framesPerBlock;
            }
        }

        public bool TryDecodeNextBlock(out AudioBlock block)
        {
            if (_blocks.Count == 0)
            {
                block = default;
                return false;
            }

            block = _blocks.Dequeue();
            return true;
        }

        public void Seek(long samplePosition)
        {
            LastSeek = samplePosition;
        }

        public void Reset()
        {
            ResetCalled = true;
        }

        public void Dispose()
        {
        }
    }

    private string GetTestInputPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "Data", "test_input.mp3");
    }

    [TestMethod]
    public async Task ComputeWaveform_ReturnsCorrectLength()
    {
        var pool = new AudioBufferPool();
        var decoder = new MockDecoder(pool, framesPerBlock: 1024, blocks: 5);

        var service = new StemWaveformService(pool);

        int segments = 10;
        var result = await service.ComputeWaveformAsync(new StemTrack(), decoder, segments);

        Assert.HasCount(segments, result);
    }

    [TestMethod]
    public async Task ComputeWaveform_CallsResetAndSeek()
    {
        var pool = new AudioBufferPool();
        var decoder = new MockDecoder(pool, framesPerBlock: 1024, blocks: 5);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(new StemTrack(), decoder, 5);

        Assert.IsTrue(decoder.ResetCalled);
        Assert.IsGreaterThanOrEqualTo(0, decoder.LastSeek);
    }

    [TestMethod]
    public async Task ComputeWaveform_ComputesNonZeroValues()
    {
        var pool = new AudioBufferPool();
        var decoder = new MockDecoder(pool, framesPerBlock: 1024, blocks: 5);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(new StemTrack(), decoder, 5);

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

    [TestMethod]
    public async Task ComputeWaveform_HandlesZeroSegments()
    {
        var pool = new AudioBufferPool();
        var decoder = new MockDecoder(pool, framesPerBlock: 1024, blocks: 5);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(new StemTrack(), decoder, 0);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task ComputeWaveform_HandlesEOF()
    {
        var pool = new AudioBufferPool();
        var decoder = new MockDecoder(pool, framesPerBlock: 1024, blocks: 1);

        var service = new StemWaveformService(pool);

        var result = await service.ComputeWaveformAsync(new StemTrack(), decoder, 5);

        Assert.HasCount(5, result);
    }

    [TestMethod]
    public void TestInputFileExists()
    {
        var path = GetTestInputPath();
        Assert.IsTrue(File.Exists(path));
    }
}
