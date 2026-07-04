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

    private StemMixSettings[]            _stemMixSettings = Array.Empty<StemMixSettings>();
    private MixerSettings?               _mixerSettings;

    private PlaybackSpeedSettings        _speedSettings   = new();
    private LoopRegion                   _loopRegion      = new();

    private long                         _currentFramePosition;
    private long                         _loopStartFrames;
    private long                         _loopEndFrames;

    private bool                         _isPlaying;
    private CancellationTokenSource?     _renderCts;
    private Task?                        _renderTask;
    private IProgressReporter<TimeSpan>? _progressReporter;

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
            {
                return _session;
            }
        }
    }

    public async Task LoadSessionAsync(PlaybackSession session, IProgressReporter<TimeSpan  > progress)
    {
        await StopAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _session = session;
            _progressReporter = progress;

            _decoders = session.StemSet.Stems
                .Select(stem => _stemDecoderFactory.Create(stem))
                .ToArray();

            _stemMixSettings = session.Mixer.Stems.ToArray();
            _mixerSettings = new MixerSettings
            {
                Stems = _stemMixSettings
            };

            _speedSettings = new PlaybackSpeedSettings
            {
                Speed = session.Speed.Speed
            };
            _timeStretchEngine.Configure(_speedSettings);

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
                _renderTask = Task.Run(() => RenderLoopAsync(_renderCts.Token));
            }

            _isPlaying = true;
            _outputDevice.Start();
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
            _outputDevice.Stop();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? ctsToCancel;
        IStemDecoder[] decodersToDispose;
        Task? renderTask;

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

            _outputDevice.Stop();

            decodersToDispose = _decoders;
            _decoders = Array.Empty<IStemDecoder>();

            renderTask = _renderTask;
            _renderTask = null;
        }

        if (ctsToCancel is not null)
        {
            ctsToCancel.Cancel();
        }

        // Do NOT wait on renderTask if we are already inside it.
        // Just let it observe cancellation and exit.
        if (renderTask is not null && !ReferenceEquals(renderTask, Task.CurrentId))
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

    public void SetSpeed(double speedFactor)
    {
        lock (_stateLock)
        {
            _speedSettings.Speed = (float)speedFactor;
            _timeStretchEngine.Configure(_speedSettings);
        }
    }

    public void SetStemEnabled(int stemIndex, bool enabled)
    {
        lock (_stateLock)
        {
            if (stemIndex < 0 || stemIndex >= _stemMixSettings.Length)
            {
                return;
            }

            var current = _stemMixSettings[stemIndex];
            _stemMixSettings[stemIndex] = new StemMixSettings
            {
                Enabled = enabled,
                GainDb = current.GainDb,
                Pan = current.Pan
            };
        }
    }

    public void SetStemGain(int stemIndex, float gainDb)
    {
        lock (_stateLock)
        {
            if (stemIndex < 0 || stemIndex >= _stemMixSettings.Length)
            {
                return;
            }

            var current = _stemMixSettings[stemIndex];
            _stemMixSettings[stemIndex] = new StemMixSettings
            {
                Enabled = current.Enabled,
                GainDb = gainDb,
                Pan = current.Pan
            };
        }
    }

    public void SetStemPan(int stemIndex, float pan)
    {
        lock (_stateLock)
        {
            if (stemIndex < 0 || stemIndex >= _stemMixSettings.Length)
            {
                return;
            }

            var current = _stemMixSettings[stemIndex];
            _stemMixSettings[stemIndex] = new StemMixSettings
            {
                Enabled = current.Enabled,
                GainDb = current.GainDb,
                Pan = pan
            };
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
                MixerSettings? mixerSettingsSnapshot;
                long loopStart;
                long loopEnd;
                bool loopEnabled;

                lock (_stateLock)
                {
                    playing               = _isPlaying;
                    decodersSnapshot      = _decoders;
                    mixerSettingsSnapshot = _mixerSettings;
                    loopStart             = _loopStartFrames;
                    loopEnd               = _loopEndFrames;
                    loopEnabled           = _loopRegion.IsEnabled;
                }

                if (!playing || decodersSnapshot.Length == 0 || mixerSettingsSnapshot is null)
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                    continue;
                }

                List<AudioBlock> stemBlocks = new();

                bool eofDetected = false;

                // Decode once, no double scanning
                var totalFrames = decodersSnapshot[0].Stem.Duration.TotalSeconds * _outputDevice.SampleRate;

                foreach (var decoder in decodersSnapshot)
                {
                    if (!decoder.TryDecodeNextBlock(out var block))
                    {
                        eofDetected = true;
                        foreach (var b in stemBlocks)
                            b.Dispose();
                        break;
                    }

                    stemBlocks.Add(block);
                }

                if (eofDetected || stemBlocks.Count == 0)
                {
                    // Report EOF progress
                    await _progressReporter!.ReportProgress(TimeSpan.FromSeconds(1.0));

                    lock (_stateLock)
                    {
                        _isPlaying = false;
                    }

                    break;
                }

                // Report progress (0..1)
                var progress = TimeSpan.FromSeconds((double)_currentFramePosition / _outputDevice.SampleRate);
                await _progressReporter!.ReportProgress(progress);

                using var mixed = _audioMixer.Mix(stemBlocks, mixerSettingsSnapshot);

                foreach (var block in stemBlocks)
                    block.Dispose();

                var nextPosition = mixed.SamplePosition + mixed.Frames;

                // Loop region handling
                if (loopEnabled && loopEnd > loopStart && nextPosition >= loopEnd)
                {
                    foreach (var decoder in decodersSnapshot)
                        decoder.Seek(loopStart);

                    lock (_stateLock)
                    {
                        _currentFramePosition = loopStart;
                    }

                    continue;
                }

                using var stretched = _timeStretchEngine.Process(mixed);

                _outputDevice.Write(stretched.Buffer.Span);

                lock (_stateLock)
                {
                    _currentFramePosition = nextPosition;
                }
            }
        }
        finally
        {
            _outputDevice.Stop();
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
