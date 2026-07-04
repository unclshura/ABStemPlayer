using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;

namespace ABStemPlayer.ViewModels;

public sealed partial class PlaybackViewModel : ObservableObject
{
    private readonly IStemPlaybackEngine  _engine;
    private readonly IStemSeparator       _separator;
    private readonly IStemDecoderFactory  _decoderFactory;
    private readonly IStemWaveformService _waveformService;

    // -----------------------------
    // Conversion mode properties
    // -----------------------------

    [ObservableProperty] private bool   _isConverting;
    [ObservableProperty] private double _progress;

    private CancellationTokenSource? _conversionCts;

    public ICommand CancelConversionCommand { get; }

    // -----------------------------
    // Existing commands
    // -----------------------------

    public ICommand OpenFileCommand    { get; }
    public ICommand PlayCommand        { get; }
    public ICommand PauseCommand       { get; }
    public ICommand StopCommand        { get; }
    public ICommand RewindCommand      { get; }
    public ICommand FastForwardCommand { get; }

    public ICommand SetPointACommand   { get; }
    public ICommand SetPointBCommand   { get; }

    // -----------------------------
    // Playback properties
    // -----------------------------

    [ObservableProperty] private bool           _loopEnabled;
    [ObservableProperty] private float          _playbackSpeed = 1f;
    [ObservableProperty] private TimeSpan       _currentTime;
    [ObservableProperty] private TimeSpan       _totalTime;
    [ObservableProperty] private MixerViewModel _mixer = null!;

    public ObservableCollection<SegmentViewModel>      Segments { get; } = new();
    public ObservableCollection<WaveformBandViewModel> Bands    { get; } = new();
    

    private TimeSpan? _loopA;
    private TimeSpan? _loopB;

    // -----------------------------
    // Constructor
    // -----------------------------

    public PlaybackViewModel(
        IStemPlaybackEngine engine,
        IStemSeparator separator,
        IStemDecoderFactory decoderFactory,
        IStemWaveformService waveformService
        )
    {
        _engine          = engine;
        _separator       = separator;
        _decoderFactory  = decoderFactory;
        _waveformService = waveformService;

        CancelConversionCommand = new RelayCommand(_ => CancelConversion());

        OpenFileCommand         = new AsyncRelayCommand(OpenFileAsync);
        PlayCommand             = new AsyncRelayCommand(() => _engine.PlayAsync());
        PauseCommand            = new AsyncRelayCommand(() => _engine.PauseAsync());
        StopCommand             = new AsyncRelayCommand(async () => { await _engine.StopAsync(); await _engine.SeekAsync(TimeSpan.Zero); });

        RewindCommand           = new AsyncRelayCommand(() => _engine.SeekAsync(CurrentTime - TimeSpan.FromSeconds(5)));
        FastForwardCommand      = new AsyncRelayCommand(() => _engine.SeekAsync(CurrentTime + TimeSpan.FromSeconds(5)));

        SetPointACommand        = new RelayCommand(_ =>
        {
            _loopA = CurrentTime;
            UpdateLoop();
        });

        SetPointBCommand        = new RelayCommand(_ =>
        {
            _loopB = CurrentTime;
            UpdateLoop();
        });

        
    }

    partial void OnCurrentTimeChanged(TimeSpan value)
    {
        foreach (var band in Bands)
        {
            band.UpdatePlaybackPosition(CurrentTime, TotalTime);
        }
    }

    partial void OnTotalTimeChanged(TimeSpan value)
    {
        foreach (var band in Bands)
        {
            band.UpdatePlaybackPosition(CurrentTime, TotalTime);
        }
    }
    // -----------------------------
    // File open + stem conversion
    //------------------------------

    private async Task OpenFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(
            (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        );

        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.mp3", "*.flac", "*.wav" }
                }
            ]
        });

        if (files.Count == 0)
            return;

        var file = files[0];

        await _engine.StopAsync();

        var session = await SplitStems(file);
        if (session == null)
            return;

        TotalTime = session.StemSet.Stems.FirstOrDefault()?.Duration ?? TimeSpan.Zero;
        CurrentTime = TimeSpan.Zero;

        await UpdateWaveForms(session);
        
        Bands.Clear();
        foreach ( var item in session.StemSet.Stems)
        {
            Bands.Add(new WaveformBandViewModel(item));
        }

        await _engine.LoadSessionAsync(session, new PlaybackProgressReporter(this));
    }

    private class PlaybackProgressReporter : IProgressReporter<TimeSpan>
    {
        private readonly PlaybackViewModel _vm;
        public PlaybackProgressReporter(PlaybackViewModel vm)
        {
            _vm = vm;
        }
        public Task ReportProgress(TimeSpan progress, CancellationToken ct)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _vm.CurrentTime = progress;
            });
            return Task.CompletedTask;
        }
    }

    private async Task UpdateWaveForms(PlaybackSession session)
    {
        IsConverting = true;
        Progress = 0;

        _conversionCts = new CancellationTokenSource();
        var ct = _conversionCts.Token;

        var stems = session.StemSet.Stems;
        var total = stems.Count;

        var reporter = new VmProgressReporter(this);

        // Thread‑safe counter
        var completed = 0;

        try
        {
            await Task.Run(async () =>
            {
                // Create parallel tasks
                var tasks = stems.Select(async stem =>
                {
                    ct.ThrowIfCancellationRequested();

                    var decoder = _decoderFactory.Create(stem);

                    var waveform = await _waveformService.ComputeWaveformAsync(stem, decoder, 200);

                    stem.Waveform = waveform;

                    // Update progress safely
                    var done = Interlocked.Increment(ref completed);
                    var p = (double)done / total;

                    await reporter.ReportProgress(p, ct);
                }).ToList();

                // Run all tasks in parallel
                await Task.WhenAll(tasks);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Progress = 0;
                IsConverting = false;
            });
            return;
        }
        catch (Exception)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Progress = 0;
                IsConverting = false;
            });
            throw;
        }

        IsConverting = false;
    }



    // -----------------------------
    // Progress reporter
    // -----------------------------

    private sealed class VmProgressReporter : IProgressReporter<double>
    {
        private readonly PlaybackViewModel _vm;

        public VmProgressReporter(PlaybackViewModel vm)
        {
            _vm = vm;
        }

        public Task ReportProgress(double progress, CancellationToken ct)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _vm.Progress = progress * 100.0;
            });

            return Task.CompletedTask;
        }
    }


    // -----------------------------
    // Stem splitting
    // -----------------------------

    private async Task<PlaybackSession?> SplitStems(IStorageFile file)
    {
        // Enter conversion mode
        IsConverting = true;
        Progress = 0;

        _conversionCts = new CancellationTokenSource();
        var ct = _conversionCts.Token;

        var outDir = Path.Combine(Path.GetDirectoryName(file.Path.LocalPath)!, "ABStemPlayer");

        StemSet? stemSet = null;

        try
        {
            // Run separation on background thread
            stemSet = await Task.Run(async () =>
            {
                try
                {
                    return await _separator.SeparateAsync(
                        new StemSeparationRequest
                        {
                            SourceFilePath = file.Path.LocalPath,
                            OutputDirectory = outDir
                        },
                        new VmProgressReporter(this),
                        ct
                    );
                }
                catch (Exception) 
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Progress = 0;
                        IsConverting = false;
                    });
                    return null;
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Progress = 0;
                IsConverting = false;
            });
        }

        // Exit conversion mode
        IsConverting = false;
        Progress = 0;

        if ( stemSet == null)
            return null;

        // Build session
        return new PlaybackSession
        {
            StemSet = stemSet,
            Mixer = new MixerSettings
            {
                Stems = stemSet.Stems.Select(GetMixerSettings).ToList()
            },
            Loop = new LoopRegion
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.Zero
            },
            Speed = new PlaybackSpeedSettings
            {
                Speed = PlaybackSpeed
            }
        };
    }

    private StemMixSettings GetMixerSettings(StemTrack stem)
    {
        var found = Mixer.Stems.FirstOrDefault(s => s.Type == stem.Type);
        if ( found != null )
            return new StemMixSettings
            {
                GainDb = found.GainDb,
                Enabled = found.Enabled,
                Pan = found.Pan
            };

        found = new StemChannelViewModel(stem.Type);
        Mixer.Stems.Add(found);
        return new StemMixSettings
        {
            GainDb = found.GainDb,
            Enabled = found.Enabled,
            Pan = found.Pan
        };
    }

    // -----------------------------
    // Cancel conversion
    // -----------------------------

    private void CancelConversion()
    {
        _conversionCts?.Cancel();
    }

    // -----------------------------
    // Loop logic
    // -----------------------------

    private void UpdateLoop()
    {
        if (_loopA.HasValue && _loopB.HasValue && LoopEnabled)
        {
            var a = _loopA.Value;
            var b = _loopB.Value;

            if (b > a)
                _engine.SetLoop(a, b);
        }
        else
        {
            _engine.ClearLoop();
        }
    }

    public void UpdateSpeed()
    {
        _engine.SetSpeed(PlaybackSpeed);
    }
}
