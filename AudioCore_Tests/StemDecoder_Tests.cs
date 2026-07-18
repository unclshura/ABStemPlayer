using AudioCore.Impl;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class StemDecoder_Tests
{
    [TestMethod]
    public async Task DecodeNextBlockAsync_ReturnsBlock()
    {
        var pool    = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader  = new FakeAudioReader(samples, 48000, 2);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1024);

        var nullableBlock = await decoder.DecodeNextBlockAsync(CancellationToken.None);

        Assert.IsNotNull(nullableBlock);

        var block = nullableBlock.Value;

        Assert.AreEqual(1024, block.Frames);
        Assert.AreEqual(0, block.Position);
        Assert.AreEqual(48000, block.SampleRate);
        Assert.AreEqual(2, block.Channels);

        block.Dispose();
    }

    [TestMethod]
    public async Task DecodeNextBlockAsync_AdvancesPosition()
    {
        var pool    = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader  = new FakeAudioReader(samples);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1000);

        var b1 = await decoder.DecodeNextBlockAsync(CancellationToken.None);
        var b2 = await decoder.DecodeNextBlockAsync(CancellationToken.None);

        Assert.IsNotNull(b1);
        Assert.IsNotNull(b2);

        Assert.AreEqual(0, b1!.Value.Position);
        Assert.AreEqual(1000, b2!.Value.Position);

        b1.Value.Dispose();
        b2.Value.Dispose();
    }

    [TestMethod]
    public async Task Seek_MovesReaderAndDecoderPosition()
    {
        var pool    = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader  = new FakeAudioReader(samples);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 500);

        decoder.Seek(2000);

        var block = await decoder.DecodeNextBlockAsync(CancellationToken.None);

        Assert.IsNotNull(block);
        Assert.AreEqual(2000, block!.Value.Position);
        Assert.AreEqual(500, block.Value.Frames);

        block.Value.Dispose();
    }

    [TestMethod]
    public async Task Reset_ReturnsToStart()
    {
        var pool    = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader  = new FakeAudioReader(samples);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 500);

        var b1 = await decoder.DecodeNextBlockAsync(CancellationToken.None);
        decoder.Reset();
        var b2 = await decoder.DecodeNextBlockAsync(CancellationToken.None);

        Assert.IsNotNull(b2);
        Assert.AreEqual(0, b2!.Value.Position);

        b1!.Value.Dispose();
        b2!.Value.Dispose();
    }

    [TestMethod]
    public async Task DecodeNextBlockAsync_ReturnsNullAtEnd()
    {
        var pool    = new AudioBufferPool();
        var samples = new float[2000];
        var reader  = new FakeAudioReader(samples, 48000, 2);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1024);

        var b1 = await decoder.DecodeNextBlockAsync(CancellationToken.None);
        Assert.IsNotNull(b1);
        b1!.Value.Dispose();

        var b2 = await decoder.DecodeNextBlockAsync(CancellationToken.None);
        if (b2 != null)
            b2!.Value.Dispose();

        var b3 = await decoder.DecodeNextBlockAsync(CancellationToken.None);
        Assert.IsNull(b3, "Decoder should return null at end of stream");
    }

    [TestMethod]
    public void Dispose_DisposesReader()
    {
        var pool    = new AudioBufferPool();
        var samples = new float[1000];
        var reader  = new FakeAudioReader(samples);
        var stem    = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem);

        decoder.Dispose();

        try
        {
            // FakeAudioReader throws ObjectDisposedException when used after Dispose
            var _ = reader.ReadAsync(new float[10].AsMemory(), CancellationToken.None).Result;
            Assert.Fail("Expected ObjectDisposedException");
        }
        catch (AssertFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Assert.IsInstanceOfType(ex, typeof(ObjectDisposedException));
        }
    }
}
