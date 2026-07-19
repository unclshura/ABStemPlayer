using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace AudioCore.Impl;

public sealed class StemPlaybackEngine : IStemPlaybackEngine, IDisposable
{
    private sealed class PipelineState : IDisposable
    {
        public IStemDecoder[] Decoders = Array.Empty<IStemDecoder>();
        public bool OutputStarted;
        public CancellationTokenSource? Cts;
        public Task? RenderTask;

        public void Dispose()
        {
            try { Cts?.Cancel(); } catch { }
            try { Cts?.Dispose(); } catch { }

            foreach (var d in Decoders)
            {
                try { d.Dispose(); } catch { }
            }

            Decoders = Array.Empty<IStemDecoder>();
        }
    }

    private readonly IStemDecoderFactory _stemDecoderFactory;
    private readonly IAudioOutputDevice  _outputDevice;
    private readonly IAudioMixer         _audioMixer;
    private readonly ITimeStretchEngine  _timeStretchEngine;

    private readonly Lock                _stateLock = new();

    private PlaybackSession?             _session;
    private MixerSettings? Mixer => _session?.Mixer;

    private LoopRegion                   _loopRegion = new();

    private long                         _decodedFramePosition;
    private long                         _loopStartFrames;
    private long                         _loopEndFrames;

    private long                         _outputFramesWritten;
    private float                        _currentSpeed = 1.0f;

    private bool IsPlaying => _outputDevice.State == PlaybackState.Playing;
    private IProgressReporter<double>?   _progressReporter;

    private PipelineState?               _pipeline;
    private long                         _pendingSeekFrames;

    public StemPlaybackEngine(
        IStemDecoderFactory stemDecoderFactory,
        IAudioOutputDevice outputDevice,
        IAudioMixer audioMixer,
        ITimeStretchEngine timeStretchEngine)
    {
        _stemDecoderFactory = stemDecoderFactory;
        _outputDevice       = outputDevice;
        _audioMixer         = audioMixer;
        _timeStretchEngine  = timeStretchEngine;
    }

    public PlaybackSession? CurrentSession
    {
        get
        {
            lock (_stateLock)
                return _session;
        }
    }

    public async Task LoadSessionAsync(PlaybackSession session, IProgressReporter<double> progress)
    {
        await StopAsync().ConfigureAwait(false);

        await _timeStretchEngine.Configure(session.Speed, CancellationToken.None).ConfigureAwait(false);

        lock (_stateLock)
        {
            _session = session;
            _progressReporter = progress;

            _currentSpeed = session.Speed.Speed;

            _loopRegion = session.Loop;
            if (_loopRegion.IsEnabled)
            {
                _loopStartFrames = TimeToFrames(_loopRegion.Start);
                _loopEndFrames = TimeToFrames(_loopRegion.End);
            }
            else
            {
                _loopStartFrames = 0;
                _loopEndFrames = 0;
            }

            _pendingSeekFrames = 0;
            _decodedFramePosition = 0;
            _outputFramesWritten = 0;
        }
    }

    public async Task PlayAsync()
    {
        // TODO: fix the pause mode
        lock (_stateLock)
        {
            if (IsPlaying || _session is null)
                return;

            if (_pipeline is not null)
            {
                return;
            }

            _pipeline = new PipelineState
            {
                Decoders = _session.StemSet.Stems
                    .Select(stem => _stemDecoderFactory.Create(stem))
                    .ToArray(),
                Cts = new CancellationTokenSource()
            };

            foreach (var d in _pipeline.Decoders)
            {
                d.Reset();
                d.Seek(_pendingSeekFrames);
            }


            _decodedFramePosition = _pendingSeekFrames;
            _outputFramesWritten  = (long)(_decodedFramePosition / Math.Max(_currentSpeed, 0.0001f));

        }

        await _timeStretchEngine.Configure(_session.Speed, _pipeline.Cts.Token).ConfigureAwait(false);
        _pipeline.RenderTask = Task.Run(() => RenderLoopAsync(_pipeline, _pipeline.Cts.Token));
    }

    public Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (!IsPlaying)
                return Task.CompletedTask;

            _outputDevice.Pause();

            if (_pipeline is not null && _pipeline.OutputStarted)
                _pipeline.OutputStarted = false;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        PipelineState? pipelineToDispose;

        lock (_stateLock)
        {
            if (!IsPlaying && _pipeline is null)
                return;

            _decodedFramePosition = 0;
            _pendingSeekFrames = 0;
            _outputFramesWritten = 0;

            pipelineToDispose = _pipeline;
            _pipeline = null;
        }

        if (pipelineToDispose is not null)
        {
            try { pipelineToDispose.Cts?.Cancel(); } catch { }

            var task = pipelineToDispose.RenderTask;
            if (task is not null && task.Id != Task.CurrentId)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            pipelineToDispose.Dispose();
        }

        _outputDevice.Stop();
    }

    public Task SeekAsync(TimeSpan position)
    {
        var frameIndex = TimeToFrames(position);

        lock (_stateLock)
        {
            _pendingSeekFrames = frameIndex;

            if (_pipeline is not null)
            {
                foreach (var d in _pipeline.Decoders)
                    d.Seek(frameIndex);

                _decodedFramePosition = frameIndex;
                _outputFramesWritten = (long)(_decodedFramePosition / Math.Max(_currentSpeed, 0.0001f));
            }
            else
            {
                _decodedFramePosition = frameIndex;
                _outputFramesWritten = (long)(_decodedFramePosition / Math.Max(_currentSpeed, 0.0001f));
            }
        }

        return Task.CompletedTask;
    }

    public async Task UpdatePlaybackSpeedAsync(PlaybackSpeedSettings settings)
    {
        lock (_stateLock)
        {
            _currentSpeed = settings.Speed;
            _outputFramesWritten = (long)(_decodedFramePosition / Math.Max(_currentSpeed, 0.0001f));
        }

        if (_pipeline?.Cts is null)
            return;

        await _timeStretchEngine.Configure(settings, _pipeline!.Cts!.Token).ConfigureAwait(false);
    }

    public Task UpdateMixerAsync(MixerSettings settings)
    {
        lock (_stateLock)
        {
            if (_session is not null)
                _session.Mixer = settings;
        }

        return Task.CompletedTask;
    }

    public void SetLoop(TimeSpan start, TimeSpan end)
    {
        lock (_stateLock)
        {
            _loopRegion = new LoopRegion
            {
                IsEnabled = true,
                Start = start,
                End = end
            };

            _loopStartFrames = TimeToFrames(start);
            _loopEndFrames = TimeToFrames(end);
        }
    }

    public void ClearLoop()
    {
        lock (_stateLock)
        {
            _loopRegion = new LoopRegion
            {
                IsEnabled = false,
                Start = TimeSpan.Zero,
                End = TimeSpan.Zero
            };

            _loopStartFrames = 0;
            _loopEndFrames = 0;
        }
    }

    private bool _decodeCompleted;

    private async Task RenderLoopAsync(PipelineState pipeline, CancellationToken token)
    {
        if (!pipeline.OutputStarted)
        {
            _outputDevice.Start();
            pipeline.OutputStarted = true;
        }

        _decodeCompleted = false;

        var decodeTask  = DecodeLoopAsync(pipeline, token);
        var stretchTask = StretchLoopAsync(pipeline, token);

        // Wait for BOTH to finish naturally
        await Task.WhenAll(decodeTask, stretchTask).ConfigureAwait(false);

        if (pipeline.OutputStarted)
        {
            _outputDevice.Stop();
            pipeline.OutputStarted = false;
        }
    }


    private async Task DecodeLoopAsync(PipelineState pipeline, CancellationToken token)
    {
        await Task.Yield();
        var stemBlocks = new List<AudioBlock>(6);

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool            playing;
                MixerSettings?  mixerSnapshot;
                IStemDecoder[]  decodersSnapshot;
                long            loopStart, loopEnd;
                bool            loopEnabled;

                lock (_stateLock)
                {
                    playing = IsPlaying;
                    mixerSnapshot = Mixer;
                    decodersSnapshot = pipeline.Decoders;
                    loopStart = _loopStartFrames;
                    loopEnd = _loopEndFrames;
                    loopEnabled = _loopRegion.IsEnabled;
                }

                if (!playing || mixerSnapshot is null || decodersSnapshot.Length == 0)
                {
                    await Task.Delay(5, token).ConfigureAwait(false);
                    continue;
                }

                if (!await ReadStemsAsync(stemBlocks, decodersSnapshot, token).ConfigureAwait(false))
                {
                    DisposeStems(stemBlocks);
                    break;
                }

                var mixed = _audioMixer.Mix(stemBlocks, mixerSnapshot);

                DisposeStems(stemBlocks);

                await _timeStretchEngine.IsReadyToAccept(token).ConfigureAwait(false);
                await _timeStretchEngine.Submit(mixed, token).ConfigureAwait(false);

                var nextPosition = mixed.SamplePosition + mixed.Frames;

                if (loopEnabled && loopEnd > loopStart && nextPosition >= loopEnd)
                {
                    lock (_stateLock)
                        _decodedFramePosition = loopEnd;
                    break;
                }

                lock (_stateLock)
                    _decodedFramePosition = nextPosition;
            }
        }
        catch { }
        finally
        {
            _decodeCompleted = true;
        }
    }

    private static void DisposeStems(List<AudioBlock> stemBlocks)
    {
        foreach (var b in stemBlocks)
            b.Dispose();
        stemBlocks.Clear();
    }

    private static async Task<bool> ReadStemsAsync(
        List<AudioBlock> stemBlocks,
        IStemDecoder[] decodersSnapshot,
        CancellationToken ct)
    {
        DisposeStems(stemBlocks);

        foreach (var decoder in decodersSnapshot)
        {
            var block = await decoder.DecodeNextBlockAsync(ct).ConfigureAwait(false);
            if (block is null)
            {
                DisposeStems(stemBlocks);
                return false;
            }

            stemBlocks.Add(block.Value);
        }

        return true;
    }

    private async Task StretchLoopAsync(PipelineState pipeline, CancellationToken token)
    {
        await Task.Yield();

        try
        {
            var gotFirstBlock = false;
            while (!token.IsCancellationRequested)
            {
                var stretched = await _timeStretchEngine.Receive(token).ConfigureAwait(false);

                if (stretched.Buffer == null)
                {
                    if (_decodeCompleted && gotFirstBlock)
                        break; // fully drained

                    await Task.Delay(1, token).ConfigureAwait(false);
                    continue;
                }

                await _outputDevice.IsReadyToAccept(token).ConfigureAwait(false);

                _outputDevice.Write(stretched.Buffer.Span);
                gotFirstBlock = true;

                lock (_stateLock)
                {
                    _outputFramesWritten += stretched.Frames;
                }


                try
                {
                    long sourceFrames;
                    lock (_stateLock)
                    {
                        sourceFrames = (long)(_outputFramesWritten * _currentSpeed);
                    }

                    double progress;
                    lock (_stateLock)
                    {
                        var total = _session?.StemSet.TotalFrames ?? 1L;
                        progress = (double)sourceFrames / Math.Max(total, 1L);
                    }

                    if (_progressReporter != null)
                        await _progressReporter.ReportProgress(progress).ConfigureAwait(false);
                }
                catch { }

                try { stretched.Dispose(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"StemPlaybackEngine: Error in PlaybackLoopAsync: {ex.Message}");
            try { pipeline.Cts?.Cancel(); } catch { }
        }
    }

    private long TimeToFrames(TimeSpan time)
    {
        return (long)(time.TotalSeconds * _outputDevice.SampleRate);
    }

    public void Dispose()
    {
        _ = StopAsync();

        if (_pipeline is not null)
        {
            try { _pipeline.Dispose(); } catch { }
            _pipeline = null;
        }
    }
}
