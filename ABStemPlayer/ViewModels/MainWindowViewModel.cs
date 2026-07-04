using System.Collections.ObjectModel;

namespace ABStemPlayer.ViewModels;

public sealed class MainWindowViewModel
{
    public PlaybackViewModel Playback { get; }
    public MixerViewModel Mixer { get; }
    public ObservableCollection<WaveformBandViewModel> Bands => Playback.Bands;

    public MainWindowViewModel(
        PlaybackViewModel playback,
        MixerViewModel mixer)
    {
        Playback       = playback;
        Mixer          = mixer;
        Playback.Mixer = mixer;
    }
}
