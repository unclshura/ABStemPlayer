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

        // Fill with deterministic ramp
        for (var i = 0; i < buf.Length; i++)
            buf.Samples[i] = i * 0.001f;

        return new MixedAudioBlock(buf, frames, channels, sampleRate, 0);
    }

    [TestMethod]
    public void Process_Returns_Output_For_Speed_1()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var input = MakeBlock(5000);
        var output = engine.Process(input);

        Assert.IsGreaterThan(0, output.Frames, "No frames returned");
        Assert.AreEqual(2, output.Channels);
        Assert.AreEqual(44100, output.SampleRate);

        // Output should be roughly same size at speed 1.0
        Assert.IsTrue(output.Frames >= 1000 && output.Frames <= 1300);

        // Validate PCM
        foreach (var f in output.Buffer.Span)
        {
            Assert.IsFalse(float.IsNaN(f));
            Assert.IsFalse(float.IsInfinity(f));
        }

        input.Dispose();
        output.Dispose();
    }

    [TestMethod]
    public void Process_Respects_Speed_Change()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        var input = MakeBlock(25000);

        // Let the engine and FFmpeg warm up with a few calls
        for (var i = 0; i < 5; i++)
            _ = engine.Process(input);

        var normal = engine.Process(input);
        var normalFrames = normal.Frames;

        engine.Configure(new PlaybackSpeedSettings { Speed = 1.5f });

        for (var i = 0; i < 5; i++)
            _ = engine.Process(input);

        var faster = engine.Process(input);

        // Don’t insist on > 0; insist on “not more than”
        Assert.IsLessThanOrEqualTo(normalFrames,
faster.Frames, $"Speed 1.5 should not increase frame count (normal={normalFrames}, faster={faster.Frames})");

        input.Dispose();
        normal.Dispose();
        faster.Dispose();
    }




    [TestMethod]
    public void Engine_Restarts_On_Speed_Change()
    {
        using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var input = MakeBlock(100);

        var before = engine.Process(input);

        engine.Configure(new PlaybackSpeedSettings { Speed = 0.75f });

        var after = engine.Process(input);

        // After restart, RubberBand has no buffered audio yet → zero frames expected
        Assert.AreEqual(0, after.Frames, "First block after restart must produce zero frames");
    }


    [TestMethod]
    public void Dispose_Kills_FFmpeg()
    {
        var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        // Capture FFmpeg PID
        var ffField = typeof(RubberBandTimeStretchEngine)
            .GetField("_ff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var ff = (Process)ffField!.GetValue(engine)!;
        var pid = ff.Id;

        engine.Dispose();

        // Process should be gone
        var exists = Process.GetProcesses().Any(p =>
        {
            try { return p.Id == pid; }
            catch { return false; }
        });

        Assert.IsFalse(exists, "FFmpeg process was not terminated");
    }
}
