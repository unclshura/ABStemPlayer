using System.Diagnostics;
using AudioCore.Impl;
using AudioCore.Interfaces;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class TimeStretchEngine_Tests
{
    private AudioBufferPool _pool = null!;

    [TestInitialize]
    public void Init()
    {
        _pool = new AudioBufferPool();
    }

    private AudioBlock MakeBlock(int frames, int channels = 2, int sampleRate = 44100)
    {
        var buf = _pool.Rent(frames * channels);
        buf.Length = frames * channels;

        for (var i = 0; i < buf.Length; i++)
            buf.Samples[i] = i * 0.001f;

        return new AudioBlock(buf, sampleRate, channels, 0);
    }

    private IReadOnlyList<AudioBlock> MakeStemSet(int stemCount, int frames)
    {
        var list = new List<AudioBlock>();
        for (int i = 0; i < stemCount; i++)
            list.Add(MakeBlock(frames));
        return list;
    }

    [TestMethod]
    public async Task Speed1_ReturnsHalfSecondBlocks()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, stemCount: 2);

        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.0f }, CancellationToken.None);

        var stems = MakeStemSet(2, 44100); // 1 second input

        await engine.SubmitStems(stems, CancellationToken.None);

        var blocks = await engine.ReceiveStems(CancellationToken.None);

        Assert.AreEqual(2, blocks.Length);

        foreach (var b in blocks)
        {
            Assert.AreEqual(22050, b.Frames);     // 0.5 seconds
            Assert.AreEqual(2, b.Channels);
            Assert.AreEqual(44100, b.SampleRate);
            Assert.IsTrue(b.Position >= 0);
        }
    }

    [TestMethod]
    public async Task FinalSegment_CanBeSmaller()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, stemCount: 2);

        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.0f }, CancellationToken.None);

        // Only 0.3 seconds of input
        var stems = MakeStemSet(2, 44100 / 3);

        await engine.SubmitStems(stems, CancellationToken.None);

        var blocks = await engine.ReceiveStems(CancellationToken.None);

        Assert.AreEqual(2, blocks.Length);

        foreach (var b in blocks)
        {
            Assert.IsTrue(b.Frames > 0);
            Assert.IsTrue(b.Frames < 22050); // final segment smaller
        }
    }

    [TestMethod]
    public async Task SpeedIncrease_ProducesFewerSourceFrames()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, stemCount: 2);

        var stems = MakeStemSet(2, 44100);

        // Speed 1.0
        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.0f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var normal = await engine.ReceiveStems(CancellationToken.None);

        long normalSource = normal[0].Position + normal[0].Frames;

        // Speed 1.5
        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.5f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var faster = await engine.ReceiveStems(CancellationToken.None);

        long fasterSource = faster[0].Position + faster[0].Frames;

        Assert.IsTrue(fasterSource > normalSource); // faster speed → source position advances more
    }

    [TestMethod]
    public async Task SpeedDecrease_ProducesMoreSourceFrames()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, stemCount: 2);

        var stems = MakeStemSet(2, 44100);

        // Speed 1.0
        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.0f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var normal = await engine.ReceiveStems(CancellationToken.None);

        long normalAdvance = (long)(normal[0].Frames * 1.0f);

        // Speed 0.5
        await engine.Configure(new PlaybackSpeedSettings { Speed = 0.5f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var slower = await engine.ReceiveStems(CancellationToken.None);

        long slowerAdvance = (long)(slower[0].Frames * 0.5f);

        Assert.IsTrue(slowerAdvance < normalAdvance); // slower speed → source position advances less
    }

    [TestMethod]
    public async Task Engine_Restarts_On_Speed_Change()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, stemCount: 2);

        var stems = MakeStemSet(2, 44100);

        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.0f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var before = await engine.ReceiveStems(CancellationToken.None);

        await engine.Configure(new PlaybackSpeedSettings { Speed = 0.75f }, CancellationToken.None);
        await engine.SubmitStems(stems, CancellationToken.None);
        var after = await engine.ReceiveStems(CancellationToken.None);

        Assert.AreNotEqual(before[0].Position, after[0].Position);
    }

}
