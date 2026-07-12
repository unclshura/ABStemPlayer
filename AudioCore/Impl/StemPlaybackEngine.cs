using System.Diagnostics;

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

    private long                         _currentFramePosition;
    private long                         _loopStartFrames;
    private long                         _loopEndFrames;

    private bool                         _isPlaying;
    private IProgressReporter<TimeSpan>? _progressReporter;

    private PipelineState?               _pipeline;
    private long                         _pendingSeekFrames;

    private readonly List<AudioBlock>    _stemBlocks = new(8);

    public StemPlaybackEngine(
        IStemDecoderFactory stemDecoderFactory,
        IAudioOutputDevice outputDevice,
        IAudioMixer audioMixer,
        ITimeStretchEngine timeStretchEngine)
    {
        _stemDecoderFactory = stemDecoderFactory;
        _outputDevice = outputDevice;
        _audioMixer = audioMixer;
        _timeStretchEngine = timeStretchEngine;
    }

    public PlaybackSession? CurrentSession
    {
        get
        {
            lock (_stateLock)
                return _session;
        }
    }

    public async Task LoadSessionAsync(PlaybackSession session, IProgressReporter<TimeSpan> progress)
    {
        await StopAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _session = session;
            _progressReporter = progress;

            _timeStretchEngine.Configure(session.Speed);

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
            _currentFramePosition = 0;
        }
    }

    public Task PlayAsync()
    {
        lock (_stateLock)
        {
            if (_isPlaying || _session is null)
                return Task.CompletedTask;

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

            _currentFramePosition = _pendingSeekFrames;

            _pipeline.RenderTask = Task.Run(() =>
                RenderLoopAsync(_pipeline, _pipeline.Cts!.Token));

            _isPlaying = true;
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (!_isPlaying)
                return Task.CompletedTask;

            _isPlaying = false;

            if (_pipeline is not null && _pipeline.OutputStarted)
            {
                _outputDevice.Stop();
                _pipeline.OutputStarted = false;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        PipelineState? pipelineToDispose;

        lock (_stateLock)
        {
            if (!_isPlaying && _pipeline is null)
                return;

            _isPlaying = false;
            _currentFramePosition = 0;
            _pendingSeekFrames = 0;

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

                _currentFramePosition = frameIndex;
            }
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

    private async Task RenderLoopAsync(PipelineState pipeline, CancellationToken ct)
    {
        var decodeTask  = DecodeLoopAsync(pipeline, ct);
        var stretchTask = StretchLoopAsync(pipeline, ct);

        await Task.WhenAny(decodeTask, stretchTask).ConfigureAwait(false);

        // When either loop ends, stop output
        if (pipeline.OutputStarted)
        {
            _outputDevice.Stop();
            pipeline.OutputStarted = false;
        }
    }

    private async Task DecodeLoopAsync(PipelineState pipeline, CancellationToken ct)
    {
        await Task.Yield();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool playing;
                MixerSettings? mixerSnapshot;
                IStemDecoder[] decodersSnapshot;
                long loopStart, loopEnd;
                bool loopEnabled;
                IProgressReporter<TimeSpan>? progressReporter;

                lock (_stateLock)
                {
                    playing          = _isPlaying;
                    mixerSnapshot    = Mixer;
                    decodersSnapshot = pipeline.Decoders;
                    loopStart        = _loopStartFrames;
                    loopEnd          = _loopEndFrames;
                    loopEnabled      = _loopRegion.IsEnabled;
                    progressReporter = _progressReporter;
                }

                if (!playing || mixerSnapshot is null || decodersSnapshot.Length == 0)
                {
                    await Task.Delay(5, ct);
                    continue;
                }

                _stemBlocks.Clear();
                bool eof = false;

                foreach (var decoder in decodersSnapshot)
                {
                    if (!decoder.TryDecodeNextBlock(out var block))
                    {
                        eof = true;
                        foreach (var b in _stemBlocks) 
                            b.Dispose();
                        _stemBlocks.Clear();
                        break;
                    }

                    _stemBlocks.Add(block);
                }

                if (eof)
                {
                    lock (_stateLock)
                        _isPlaying = false;
                    break;
                }

                var mixed = _audioMixer.Mix(_stemBlocks, mixerSnapshot);

                foreach (var b in _stemBlocks)
                    b.Dispose();
                _stemBlocks.Clear();

                await _timeStretchEngine.Submit(mixed, ct).ConfigureAwait(false);

                var progress = TimeSpan.FromSeconds(
                (double)_currentFramePosition / _outputDevice.SampleRate);

                if (progressReporter != null)
                    await progressReporter.ReportProgress(progress);

                var nextPosition = mixed.SamplePosition + mixed.Frames;

                if (loopEnabled && loopEnd > loopStart && nextPosition >= loopEnd)
                {
                    lock (_stateLock)
                    {
                        _currentFramePosition = loopEnd;
                        _isPlaying = false;
                    }
                    break;
                }

                lock (_stateLock)
                    _currentFramePosition = nextPosition;
            }
        }
        catch { }
    }

    private async Task StretchLoopAsync(PipelineState pipeline, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stretched = await _timeStretchEngine.Receive(ct).ConfigureAwait(false);

                if (stretched.Buffer == null)
                {
                    await Task.Delay(1, ct);
                    continue;
                }

                if (!pipeline.OutputStarted)
                {
                    _outputDevice.Start();
                    pipeline.OutputStarted = true;
                }

                _outputDevice.Write(stretched.Buffer.Span);
            }
        }
        catch(Exception ex) 
        {
            Debug.WriteLine($"StemPlaybackEngine: Error in StretchLoopAsync: {ex.Message}");
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
