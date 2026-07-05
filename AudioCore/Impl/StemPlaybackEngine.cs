namespace AudioCore.Impl;

public sealed class StemPlaybackEngine : IStemPlaybackEngine, IDisposable
{
    private readonly IStemDecoderFactory _stemDecoderFactory;
    private readonly IAudioOutputDevice  _outputDevice;
    private readonly IAudioMixer         _audioMixer;
    private readonly ITimeStretchEngine  _timeStretchEngine;

    private readonly Lock                _stateLock       = new();

    private PlaybackSession?             _session;
    private IStemDecoder[]               _decoders        = Array.Empty<IStemDecoder>();

    private MixerSettings? Mixer => _session?.Mixer;

    private LoopRegion                   _loopRegion      = new();

    private long                         _currentFramePosition;
    private long                         _loopStartFrames;
    private long                         _loopEndFrames;

    private bool                         _isPlaying;
    private bool                         _outputStarted;
    private CancellationTokenSource?     _renderCts;
    private Task?                        _renderTask;
    private IProgressReporter<TimeSpan>? _progressReporter;

    // Reused per-block list, no per-frame allocation
    private readonly List<AudioBlock>    _stemBlocks      = new(8);

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
            {
                return _session;
            }
        }
    }

    public async Task LoadSessionAsync(PlaybackSession session, IProgressReporter<TimeSpan> progress)
    {
        await StopAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _session = session;
            _progressReporter = progress;

            _decoders = session.StemSet.Stems
                .Select(stem => _stemDecoderFactory.Create(stem))
                .ToArray();

            _timeStretchEngine.Configure(_session.Speed);

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

            _currentFramePosition = 0;

            foreach (var decoder in _decoders)
            {
                decoder.Reset();
            }
        }
    }

    public Task PlayAsync()
    {
        lock (_stateLock)
        {
            if (_isPlaying)
            {
                return Task.CompletedTask;
            }

            if (_renderTask is null || _renderTask.IsCompleted)
            {
                _renderCts?.Dispose();
                _renderCts = new CancellationTokenSource();
                _outputStarted = false;
                _renderTask = Task.Run(() => RenderLoopAsync(_renderCts.Token));
            }

            _isPlaying = true;
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (!_isPlaying)
            {
                return Task.CompletedTask;
            }

            _isPlaying = false;

            if (_outputStarted)
            {
                _outputDevice.Stop();
                _outputStarted = false;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? ctsToCancel;
        IStemDecoder[] decodersToDispose;
        Task? renderTask;
        bool outputStarted;

        lock (_stateLock)
        {
            if (!_isPlaying && _renderTask is null)
            {
                return;
            }

            _isPlaying = false;
            _currentFramePosition = 0;

            ctsToCancel = _renderCts;
            _renderCts = null;

            outputStarted = _outputStarted;
            _outputStarted = false;

            if (outputStarted)
            {
                _outputDevice.Stop();
            }

            decodersToDispose = _decoders;
            _decoders = Array.Empty<IStemDecoder>();

            renderTask = _renderTask;
            _renderTask = null;
        }

        if (ctsToCancel is not null)
        {
            ctsToCancel.Cancel();
        }

        if (renderTask is not null && renderTask.Id != Task.CurrentId)
        {
            try
            {
                await renderTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var decoder in decodersToDispose)
        {
            decoder.Dispose();
        }
    }

    public Task SeekAsync(TimeSpan position)
    {
        var frameIndex = TimeToFrames(position);

        lock (_stateLock)
        {
            if (_session is null || _decoders.Length == 0)
            {
                return Task.CompletedTask;
            }

            _currentFramePosition = frameIndex;

            foreach (var decoder in _decoders)
            {
                decoder.Seek(frameIndex);
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

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool playing;
                IStemDecoder[] decodersSnapshot;
                long loopStart;
                long loopEnd;
                bool loopEnabled;
                MixerSettings? mixerSnapshot;
                IProgressReporter<TimeSpan>? progressReporter;

                lock (_stateLock)
                {
                    playing = _isPlaying;
                    decodersSnapshot = _decoders;
                    loopStart = _loopStartFrames;
                    loopEnd = _loopEndFrames;
                    loopEnabled = _loopRegion.IsEnabled;
                    mixerSnapshot = Mixer;
                    progressReporter = _progressReporter;
                }

                if (!playing || decodersSnapshot.Length == 0 || mixerSnapshot is null)
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                    continue;
                }

                _stemBlocks.Clear();
                var eofDetected = false;

                foreach (var decoder in decodersSnapshot)
                {
                    if (!decoder.TryDecodeNextBlock(out var block))
                    {
                        eofDetected = true;

                        for (var i = 0; i < _stemBlocks.Count; i++)
                        {
                            _stemBlocks[i].Dispose();
                        }

                        _stemBlocks.Clear();
                        break;
                    }

                    _stemBlocks.Add(block);
                }

                if (eofDetected || _stemBlocks.Count == 0)
                {
                    if (progressReporter is not null)
                    {
                        await progressReporter.ReportProgress(TimeSpan.FromSeconds(1.0));
                    }

                    lock (_stateLock)
                    {
                        _isPlaying = false;
                    }

                    break;
                }

                var progress = TimeSpan.FromSeconds((double)_currentFramePosition / _outputDevice.SampleRate);
                if (progressReporter is not null)
                {
                    await progressReporter.ReportProgress(progress);
                }

                using var mixed = _audioMixer.Mix(_stemBlocks, mixerSnapshot);

                for (var i = 0; i < _stemBlocks.Count; i++)
                {
                    _stemBlocks[i].Dispose();
                }
                _stemBlocks.Clear();

                using var stretched = _timeStretchEngine.Process(mixed);

                if (stretched.Buffer != null)
                {
                    if (!_outputStarted)
                    {
                        _outputDevice.Start();
                        _outputStarted = true;
                    }

                    _outputDevice.Write(stretched.Buffer.Span);
                }

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
                {
                    _currentFramePosition = nextPosition;
                }
            }
        }
        finally
        {
            if (_outputStarted)
            {
                _outputDevice.Stop();
                _outputStarted = false;
            }
        }
    }

    private long TimeToFrames(TimeSpan time)
    {
        return (long)(time.TotalSeconds * _outputDevice.SampleRate);
    }

    public void Dispose()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();

        foreach (var decoder in _decoders)
        {
            decoder.Dispose();
        }

        _decoders = Array.Empty<IStemDecoder>();
    }
}
