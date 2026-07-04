using AudioCore.Impl;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class StemDecoder_Tests
{
    [TestMethod]
    public void TryDecodeNextBlock_ReturnsBlock()
    {
        var pool = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray(); // 1 sec stereo
        var reader = new FakeAudioReader(samples, 48000, 2);
        var stem = new StemTrack{ Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1024);

        var ok = decoder.TryDecodeNextBlock(out var block);

        Assert.IsTrue(ok);
        Assert.AreEqual(1024, block.Frames);
        Assert.AreEqual(0, block.Position);
        Assert.AreEqual(48000, block.SampleRate);
        Assert.AreEqual(2, block.Channels);

        block.Dispose();
    }

    [TestMethod]
    public void TryDecodeNextBlock_AdvancesPosition()
    {
        var pool = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader = new FakeAudioReader(samples);
        var stem = new StemTrack{ Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1000);

        decoder.TryDecodeNextBlock(out var b1);
        decoder.TryDecodeNextBlock(out var b2);

        Assert.AreEqual(0, b1.Position);
        Assert.AreEqual(1000, b2.Position);

        b1.Dispose();
        b2.Dispose();
    }

    [TestMethod]
    public void Seek_MovesReaderAndDecoderPosition()
    {
        var pool = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader = new FakeAudioReader(samples);
        var stem = new StemTrack{ Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 500);

        decoder.Seek(2000); // sample position

        decoder.TryDecodeNextBlock(out var block);

        Assert.AreEqual(2000, block.Position);
        Assert.AreEqual(500, block.Frames);

        block.Dispose();
    }

    [TestMethod]
    public void Reset_ReturnsToStart()
    {
        var pool = new AudioBufferPool();
        var samples = Enumerable.Range(0, 48000).Select(i => (float)i).ToArray();
        var reader = new FakeAudioReader(samples);
        var stem = new StemTrack{ Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 500);

        decoder.TryDecodeNextBlock(out var b1);
        decoder.Reset();
        decoder.TryDecodeNextBlock(out var b2);

        Assert.AreEqual(0, b2.Position);

        b1.Dispose();
        b2.Dispose();
    }

    [TestMethod]
    public void TryDecodeNextBlock_ReturnsFalseAtEnd()
    {
        var pool = new AudioBufferPool();
        var samples = new float[2000]; // small buffer
        var reader = new FakeAudioReader(samples, 48000, 2);
        var stem = new StemTrack { Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem, blockSize: 1024);

        // First block: should succeed
        Assert.IsTrue(decoder.TryDecodeNextBlock(out var b1));
        Assert.IsNotNull(b1.Buffer);
        b1.Dispose();

        // Second block: may succeed or partially succeed
        decoder.TryDecodeNextBlock(out var b2);
        if (b2.Buffer != null)
            b2.Dispose();

        // Third block: MUST fail
        var ok = decoder.TryDecodeNextBlock(out var b3);

        Assert.IsFalse(ok, "Decoder should return false at end of stream");

        // IMPORTANT: do NOT touch b3.Buffer — it is null
    }


    [TestMethod]
    public void Dispose_DisposesReader()
    {
        var pool = new AudioBufferPool();
        var samples = new float[1000];
        var reader = new FakeAudioReader(samples);
        var stem = new StemTrack{ Name = "test", FilePath = "file.wav" };

        var decoder = new StemDecoder(reader, pool, stem);

        decoder.Dispose();

        try
        {
            // This must throw
            reader.Read(new float[10], 0, 10);
            Assert.Fail("Expected ObjectDisposedException");
        }
        catch(AssertFailedException )
        {
            throw;
        }
        catch (Exception ex)
        {
            Assert.IsInstanceOfType(ex, typeof(ObjectDisposedException));
        }
    }


}
