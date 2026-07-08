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

    private MixedAudioBlock MakeBlock(int frames, int channels = 2, int sampleRate = 44100)
    {
        var buf = _pool.Rent(frames * channels);
        buf.Length = frames * channels;

        for (var i = 0; i < buf.Length; i++)
            buf.Samples[i] = i * 0.001f;

        return new MixedAudioBlock(buf, frames, channels, sampleRate, 0);
    }

    [TestMethod]
    public async Task Process_Returns_Output_For_Speed_1()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var input = MakeBlock(5000);

        await engine.Submit(input);
        var output = await engine.Receive();

        Assert.IsGreaterThan(0, output.Frames);
        Assert.AreEqual(2, output.Channels);
        Assert.AreEqual(44100, output.SampleRate);
        Assert.AreEqual(5000, output.Frames);

        foreach (var f in output.Buffer.Span)
        {
            Assert.IsFalse(float.IsNaN(f));
            Assert.IsFalse(float.IsInfinity(f));
        }

        input.Dispose();
        output.Dispose();
    }

    [TestMethod]
    public async Task Process_Respects_Speed_Increase()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        var input = MakeBlock(1000);

        for (var i = 0; i < 25; i++)
            await engine.Submit(input);

        var normalFrames = 0;
        while (true)
        {
            using var data = await engine.Receive();
            normalFrames += data.Frames;
            if (data.Buffer == null)
                break;
        }


        engine.Configure(new PlaybackSpeedSettings { Speed = 1.5f });


        for (var i = 0; i < 25; i++)
            await engine.Submit(input);

        var fasterFrames = 0;
        while (true)
        {
            using var data = await engine.Receive();
            fasterFrames += data.Frames;
            if (data.Buffer == null)
                break;
        }

        Assert.IsLessThanOrEqualTo(normalFrames, fasterFrames);

        input.Dispose();
    }

    [TestMethod]
    public async Task Process_Respects_Speed_Decrease()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        var input = MakeBlock(1000);

        for (var i = 0; i < 25; i++)
            await engine.Submit(input);

        var normalFrames = 0;
        while (true)
        {
            using var data = await engine.Receive();
            normalFrames += data.Frames;
            if (data.Buffer == null)
                break;
        }

        engine.Configure(new PlaybackSpeedSettings { Speed = 0.5f });

        for (var i = 0; i < 25; i++)
            await engine.Submit(input);

        var slowerFrames = 0;
        while (true)
        {
            using var data = await engine.Receive();
            slowerFrames += data.Frames;
            if (data.Buffer == null)
                break;
        }

        Assert.IsGreaterThanOrEqualTo(normalFrames, slowerFrames);

        input.Dispose();
    }

    [TestMethod]
    public async Task Engine_Restarts_On_Speed_Change()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var input = MakeBlock(100);

        await engine.Submit(input);
        var before = await engine.Receive();

        engine.Configure(new PlaybackSpeedSettings { Speed = 0.75f });

        await engine.Submit(input);
        var after = await engine.Receive();

        Assert.AreEqual(0, after.Frames);
    }

    [TestMethod]
    public void Dispose_Kills_FFmpeg()
    {
        var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var ffField = typeof(RubberBandTimeStretchEngine)
            .GetField("_ff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var ff = (Process?)ffField!.GetValue(engine);
        var pid = ff?.Id ?? -1;

        engine.Dispose();

        var exists = Process.GetProcesses().Any(p =>
        {
            try { return p.Id == pid; }
            catch { return false; }
        });

        Assert.IsFalse(exists);
    }
}
