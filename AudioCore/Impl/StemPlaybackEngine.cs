using System.Diagnostics;
using NAudio.Wave;
using static AudioCore.Models.Tracer;

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
        Trace(session);

        await StopAsync().ConfigureAwait(false);

        await _timeStretchEngine.Configure(session.Speed, CancellationToken.None).ConfigureAwait(false);

        lock (_stateLock)
        {
            _session = session;
            _progressReporter = progress;

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
        }
    }

    public async Task PlayAsync()
    {
        Trace();

        lock (_stateLock)
        {
            if (IsPlaying || _session is null)
                return;

            if (_pipeline is not null)
                return;

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
        }

        _pipeline.RenderTask = Task.Run(() => RenderLoopAsync(_pipeline, _pipeline.Cts.Token));
    }

    public Task PauseAsync()
    {
        Trace();

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
        Trace();
        PipelineState? pipelineToDispose;

        Trace();

        lock (_stateLock)
        {
            if (!IsPlaying && _pipeline is null)
                return;

            _decodedFramePosition = 0;
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
        Trace(position);

        var frameIndex = TimeToFrames(position);

        lock (_stateLock)
        {
            _pendingSeekFrames = frameIndex;

            if (_pipeline is not null)
            {
                foreach (var d in _pipeline.Decoders)
                    d.Seek(frameIndex);

                _decodedFramePosition = frameIndex;
            }
            else
            {
                _decodedFramePosition = frameIndex;
            }
        }

        return Task.CompletedTask;
    }

    public async Task UpdatePlaybackSpeedAsync(PlaybackSpeedSettings settings)
    {
        Trace(settings);

        await _timeStretchEngine.Configure(settings, _pipeline?.Cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    public Task UpdateMixerAsync(MixerSettings settings)
    {
        Trace(settings);

        lock (_stateLock)
        {
            if (_session is not null)
                _session.Mixer = settings;
        }

        return Task.CompletedTask;
    }

    public void SetLoop(TimeSpan start, TimeSpan end)
    {
        Trace(start, end);

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
        Trace();
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
        Trace(pipeline);

        if (!pipeline.OutputStarted)
        {
            _outputDevice.Start();
            pipeline.OutputStarted = true;
        }

        _decodeCompleted = false;

        var decodeTask  = DecodeLoopAsync(pipeline, token);
        var stretchTask = StretchLoopAsync(pipeline, token);

        await Task.WhenAll(decodeTask, stretchTask).ConfigureAwait(false);

        if (pipeline.OutputStarted)
        {
            _outputDevice.Stop();
            pipeline.OutputStarted = false;
        }
    }

    private async Task DecodeLoopAsync(PipelineState pipeline, CancellationToken token)
    {
        Trace(pipeline);

        await Task.Yield();
        var stemBlocks = new List<AudioBlock>(6);

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool           playing;
                IStemDecoder[] decodersSnapshot;
                long           loopStart, loopEnd;
                bool           loopEnabled;

                lock (_stateLock)
                {
                    playing = IsPlaying;
                    decodersSnapshot = pipeline.Decoders;
                    loopStart = _loopStartFrames;
                    loopEnd = _loopEndFrames;
                    loopEnabled = _loopRegion.IsEnabled;
                }

                if (!playing || decodersSnapshot.Length == 0)
                {
                    await Task.Delay(5, token).ConfigureAwait(false);
                    continue;
                }

                if (!await ReadStemsAsync(stemBlocks, decodersSnapshot, token).ConfigureAwait(false))
                {
                    DisposeStems(stemBlocks);
                    break;
                }

                // Submit raw stems to time-stretch engine
                await _timeStretchEngine.IsReadyToAcceptStems(token).ConfigureAwait(false);
                await _timeStretchEngine.SubmitStems(stemBlocks, token).ConfigureAwait(false);

                // Use first stem for position tracking
                var first = stemBlocks[0];
                var nextPosition = first.Position + first.Frames;

                DisposeStems(stemBlocks);

                if (loopEnabled && loopEnd > loopStart && nextPosition >= loopEnd)
                {
                    // Rewind decoders to the loop start and continue decoding so playback loops
                    foreach (var d in decodersSnapshot)
                    {
                        try { d.Seek(loopStart); } catch { }
                    }

                    lock (_stateLock)
                        _decodedFramePosition = loopStart;

                    // continue decoding from the loop start
                    continue;
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
        Trace(pipeline);

        await Task.Yield();
        Debug.Assert(Mixer != null, "Mixer settings should be set before starting playback.");

        try
        {
            var gotFirstBlock = false;
            while (!token.IsCancellationRequested)
            {
                var stretchedBlocks = await _timeStretchEngine.ReceiveStems(token).ConfigureAwait(false);

                if (stretchedBlocks == null || stretchedBlocks.Length == 0 || stretchedBlocks[0].Buffer == null)
                {
                    if (_decodeCompleted && gotFirstBlock)
                        break; // fully drained

                    await Task.Delay(1, token).ConfigureAwait(false);
                    continue;
                }

                MixerSettings? mixerSnapshot;
                lock (_stateLock)
                    mixerSnapshot = Mixer;

                var mixed = _audioMixer.Mix(stretchedBlocks, mixerSnapshot);

                await _outputDevice.IsReadyToAccept(token).ConfigureAwait(false);

                _outputDevice.Write(mixed.Buffer.Span);
                gotFirstBlock = true;

                try
                {
                    double progress;
                    lock (_stateLock)
                    {
                        var total = _session?.StemSet.TotalFrames ?? 1L;
                        progress = (double)mixed.Position / Math.Max(total, 1L);
                    }

                    if (_progressReporter != null)
                        await _progressReporter.ReportProgress(progress).ConfigureAwait(false);
                }
                catch { }

                try { mixed.Dispose(); } catch { }
                foreach (var b in stretchedBlocks)
                {
                    try { b.Dispose(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"StemPlaybackEngine: Error in StretchLoopAsync: {ex.Message}");
            try { pipeline.Cts?.Cancel(); } catch { }
        }
    }

    private long TimeToFrames(TimeSpan time)
    {
        return (long)(time.TotalSeconds * _outputDevice.SampleRate);
    }

    public void Dispose()
    {
        Trace();

        _ = StopAsync();

        if (_pipeline is not null)
        {
            try { _pipeline.Dispose(); } catch { }
            _pipeline = null;
        }
    }
}
