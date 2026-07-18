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
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);

        var input = MakeBlock(5000);

        await engine.Submit(input, CancellationToken.None);
        var output = await engine.Receive(CancellationToken.None);

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
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        using var input = MakeBlock(44100);
        using var cts = new CancellationTokenSource();

        // -----------------------------
        // Phase 1: speed = 1.0
        // -----------------------------
        var normalFrames = 0;

        const int NumberOfIterations = 5;

        var submitTask1 = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            for (var i = 0; i < NumberOfIterations; i++)
                await engine.Submit(input, ts.Token);
        });

        var receiveTask1 = Task.Run(async () =>
        {
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
                
                using var data = await engine.Receive(ts.Token);
                if (data.Buffer == null)
                    break;

                normalFrames += data.Frames;
            }
        });

        await Task.WhenAll(submitTask1, receiveTask1);

        Debug.WriteLine($"Normal frames: {normalFrames}");

        // -----------------------------
        // Phase 2: speed = 1.5
        // -----------------------------
        await engine.Configure(new PlaybackSpeedSettings { Speed = 1.5f }, cts.Token);

        var fasterFrames = 0;

        var submitTask2 = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            for (var i = 0; i < NumberOfIterations; i++)
                await engine.Submit(input, ts.Token);
        });

        var receiveTask2 = Task.Run(async () =>
        {
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                using var data = await engine.Receive(ts.Token);
                if (data.Buffer == null)
                    break;

                fasterFrames += data.Frames;
            }
        });

        await Task.WhenAll(submitTask2, receiveTask2);

        Debug.WriteLine($"Faster frames: {fasterFrames}");

        // -----------------------------
        // Assertion
        // -----------------------------
        Assert.IsLessThan(fasterFrames, normalFrames);

        cts.Cancel();
    }


    [TestMethod]
    public async Task Process_Respects_Speed_Decrease()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        using var input = MakeBlock(44100);
        using var cts = new CancellationTokenSource();

        // -----------------------------
        // Phase 1: speed = 1.0
        // -----------------------------
        var normalFrames = 0;

        const int NumberOfIterations = 5;

        var submitTask1 = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            for (var i = 0; i < NumberOfIterations; i++)
                await engine.Submit(input, ts.Token);
        });

        var receiveTask1 = Task.Run(async () =>
        {
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                using var data = await engine.Receive(ts.Token);
                if (data.Buffer == null)
                    break;

                normalFrames += data.Frames;
            }
        });

        await Task.WhenAll(submitTask1, receiveTask1);

        Debug.WriteLine($"Normal frames: {normalFrames}");

        // -----------------------------
        // Phase 2: speed = 0.5
        // -----------------------------
        await engine.Configure(new PlaybackSpeedSettings { Speed = 0.5f }, cts.Token);

        var slowerFrames = 0;

        var submitTask2 = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            for (var i = 0; i < NumberOfIterations; i++)
                await engine.Submit(input, ts.Token);
        });

        var receiveTask2 = Task.Run(async () =>
        {
            while (true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var ts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                using var data = await engine.Receive(ts.Token);
                if (data.Buffer == null)
                    break;

                slowerFrames += data.Frames;
            }
        });

        await Task.WhenAll(submitTask2, receiveTask2);

        Debug.WriteLine($"Slower frames: {slowerFrames}");

        // -----------------------------
        // Assertion
        // -----------------------------
        Assert.IsLessThan(slowerFrames, normalFrames);

        cts.Cancel();
    }

    [TestMethod]
    public async Task Engine_Restarts_On_Speed_Change()
    {
        await using var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        using var cts = new CancellationTokenSource();

        await engine.Configure(new PlaybackSpeedSettings { Speed = 1f }, cts.Token);

        var input = MakeBlock(44100);

        await engine.Submit(input, cts.Token);
        var before = await engine.Receive(cts.Token);

        await engine.Configure(new PlaybackSpeedSettings { Speed = 0.75f }, cts.Token);

        await engine.Submit(input, cts.Token);
        var after = await engine.Receive(cts.Token);

        Assert.AreNotEqual(0, before.Frames);
        Assert.AreNotEqual(0, after.Frames);
        Assert.AreNotEqual(before.Frames, after.Frames);

        cts.Cancel();
    }

    [TestMethod]
    public async Task Dispose_Kills_FFmpeg()
    {
        var engine = new RubberBandTimeStretchEngine(_pool, 44100, 2);
        using var cts = new CancellationTokenSource();

        var ffField = typeof(RubberBandTimeStretchEngine)
            .GetField("_ff", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var ff = (Process?)ffField!.GetValue(engine);
        var pid = ff?.Id ?? -1;

        await engine.DisposeAsync();

        var exists = Process.GetProcesses().Any(p =>
        {
            try { return p.Id == pid; }
            catch { return false; }
        });

        Assert.IsFalse(exists);
        cts.Cancel();
    }
}
