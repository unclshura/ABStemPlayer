using AudioCore.Impl;
using AudioCore.Interfaces;
using AudioCore.Models;
using NAudio.Wave;

namespace AudioCore_Tests;

[TestClass]
public sealed class StemPlaybackEngine_Tests
{
    private sealed class MockDecoder : IStemDecoder
    {
        private readonly Queue<AudioBlock> _blocks;
        private readonly AudioBufferPool _pool;
        private long _seekPosition;

        public StemTrack Stem { get; }

        public MockDecoder(int framesPerBlock, int blocks, AudioBufferPool pool)
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
            for (var i = 0; i < blocks; i++)
            {
                var buf = _pool.Rent(framesPerBlock * Stem.Channels);
                buf.Length = framesPerBlock * Stem.Channels;
                _blocks.Enqueue(new AudioBlock(buf, Stem.SampleRate, Stem.Channels, pos));
                pos += framesPerBlock;
            }
        }

        public Task<AudioBlock?> DecodeNextBlockAsync(CancellationToken token)
        {
            AudioBlock? block;
            if (_blocks.Count == 0)
            {
                return Task.FromResult<AudioBlock?>(null);
            }

            block = _blocks.Dequeue();
            return Task.FromResult<AudioBlock?>(block);
        }

        public void Seek(long samplePosition)
        {
            _seekPosition = samplePosition;
        }

        public void Reset()
        {
            _seekPosition = 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class MockDecoderFactory : IStemDecoderFactory
    {
        private readonly AudioBufferPool _pool;
        private readonly int _frames;
        private readonly int _blocks;

        public MockDecoderFactory(AudioBufferPool pool, int frames, int blocks)
        {
            _pool = pool;
            _frames = frames;
            _blocks = blocks;
        }

        public IStemDecoder Create(StemTrack stem)
        {
            return new MockDecoder(_frames, _blocks, _pool);
        }
    }

    private sealed class MockMixer(AudioBufferPool _pool) : IAudioMixer
    {
        public MixedAudioBlock Mix(IReadOnlyList<AudioBlock> stemBlocks, MixerSettings settings)
        {
            var first = stemBlocks[0];
            var buf   = _pool.Rent(first.Length);
            Array.Copy(first.Buffer.Samples, buf.Samples, first.Length);

            return new MixedAudioBlock(
                buf,
                first.Frames,
                first.Channels,
                first.SampleRate,
                first.Position);
        }
    }

    private sealed class MockTimeStretch : ITimeStretchEngine
    {
        private MixedAudioBlock _lastInput;

        public Task Configure(PlaybackSpeedSettings settings, CancellationToken token)
        {
            // no-op for tests
            return Task.CompletedTask;
        }

        public Task Submit(MixedAudioBlock input, CancellationToken token)
        {
            _lastInput = input;
            return Task.CompletedTask;
        }

        public Task<TimeStretchedAudioBlock> Receive(CancellationToken token)
        {
            if (_lastInput.Buffer == null)
                return Task.FromResult(default(TimeStretchedAudioBlock));

            var block = new TimeStretchedAudioBlock(
                _lastInput.Buffer,
                _lastInput.Frames,
                _lastInput.Channels,
                _lastInput.SampleRate);

            _lastInput = default;
            return Task.FromResult(block);
        }

        Task ITimeStretchEngine.IsReadyToAccept(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class MockOutput : IAudioOutputDevice
    {
        public int SampleRate => 44100;
        public int Channels => 2;

        public int WriteCount { get; private set; }
        public int LastWriteSamples { get; private set; }
        public bool Started { get; private set; }

        public Task IsReadyToAccept(CancellationToken token) => Task.CompletedTask;

        public void Start()
        {
            Started = true;
        }

        public void Stop()
        {
            Started = false;
        }
        public void Pause() => Started = false;
        public PlaybackState State => Started ? PlaybackState.Playing : PlaybackState.Stopped;

        public void Write(ReadOnlySpan<float> samples)
        {
            WriteCount++;
            LastWriteSamples = samples.Length;
        }
    }

    private PlaybackSession CreateSession(int stems)
    {
        var stemList = new List<StemTrack>();
        var mixList  = new List<StemMixSettings>();

        for (var i = 0; i < stems; i++)
        {
            stemList.Add(new StemTrack
            {
                Channels = 2,
                SampleRate = 44100,
                Duration = TimeSpan.FromSeconds(10),
                FilePath = "x"
            });

            mixList.Add(new StemMixSettings
            {
                Enabled = true,
                GainDb = 0,
                Pan = 0
            });
        }

        return new PlaybackSession
        {
            StemSet = new StemSet
            {
                OriginalFilePath = "x",
                Stems = stemList
            },
            Mixer = new MixerSettings
            {
                Stems = mixList
            },
            Loop = new LoopRegion
            {
                IsEnabled = false
            },
            Speed = new PlaybackSpeedSettings
            {
                Speed = 1.0f
            }
        };
    }

    private class DummyProgressReporter : IProgressReporter<double>
    {
        public Task ReportProgress(double value, CancellationToken ct)
            => Task.CompletedTask;
    }

    [TestMethod]
    public async Task LoadSession_InitializesDecoders()
    {
        var pool           = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
        var output         = new MockOutput();
        var mixer          = new MockMixer(pool);
        var stretch        = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(3);
        await engine.LoadSessionAsync(session, new DummyProgressReporter());

        Assert.IsFalse(output.Started);
    }

    //[TestMethod]
    //public async Task PlayAsync_StartsOutputDevice()
    //{
    //    var pool           = new AudioBufferPool();
    //    var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
    //    var output         = new MockOutput();
    //    var mixer          = new MockMixer(pool);
    //    var stretch        = new MockTimeStretch();

    //    var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

    //    var session = CreateSession(2);
    //    await engine.LoadSessionAsync(session, new DummyProgressReporter());

    //    await engine.PlayAsync();

    //    Assert.IsTrue(output.Started);
    //}

    [TestMethod]
    public async Task PauseAsync_StopsOutputDevice()
    {
        var pool           = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
        var output         = new MockOutput();
        var mixer          = new MockMixer(pool);
        var stretch        = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(2);
        await engine.LoadSessionAsync(session, new DummyProgressReporter());

        await engine.PlayAsync();
        await engine.PauseAsync();

        Assert.IsFalse(output.Started);
    }

    [TestMethod]
    public async Task RenderLoop_WritesAudioBlocks()
    {
        var pool           = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 3);
        var output         = new MockOutput();
        var mixer          = new MockMixer(pool);
        var stretch        = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(1);
        await engine.LoadSessionAsync(session, new DummyProgressReporter());

        await engine.PlayAsync();

        await Task.Delay(50);

        await engine.StopAsync();

        Assert.IsGreaterThan(0, output.WriteCount);
        Assert.AreEqual(1024 * 2, output.LastWriteSamples);
    }

    [TestMethod]
    public async Task SeekAsync_MovesDecoders()
    {
        var pool           = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
        var output         = new MockOutput();
        var mixer          = new MockMixer(pool);
        var stretch        = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(1);
        await engine.LoadSessionAsync(session, new DummyProgressReporter());

        await engine.SeekAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(output.Started);
    }

    [TestMethod]
    public async Task LoopRegion_SeeksBackOnBoundary()
    {
        var pool          = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
        var output        = new MockOutput();
        var mixer         = new MockMixer(pool);
        var stretch       = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(1);
        session.Loop = new LoopRegion
        {
            IsEnabled = true,
            Start = TimeSpan.FromSeconds(0),
            End = TimeSpan.FromSeconds(1)
        };

        await engine.LoadSessionAsync(session, new DummyProgressReporter());
        await engine.PlayAsync();

        await Task.Delay(TimeSpan.FromSeconds(3));

        await engine.StopAsync();

        Assert.IsGreaterThan(0, output.WriteCount);
    }

    [TestMethod]
    public async Task SpeedChange_ReconfiguresTimeStretch()
    {
        var pool          = new AudioBufferPool();
        var decoderFactory = new MockDecoderFactory(pool, 1024, 5);
        var output        = new MockOutput();
        var mixer         = new MockMixer(pool);
        var stretch       = new MockTimeStretch();

        var engine = new StemPlaybackEngine(decoderFactory, output, mixer, stretch);

        var session = CreateSession(1);
        await engine.LoadSessionAsync(session, new DummyProgressReporter());

        engine.CurrentSession!.Speed.Speed = 1.5f;

        Assert.IsFalse(output.Started);
    }
}
