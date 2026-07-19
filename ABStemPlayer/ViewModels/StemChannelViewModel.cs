namespace ABStemPlayer.ViewModels;

public partial class StemChannelViewModel : ObservableObject
{
    public StemType Type { get; }

    [ObservableProperty] private bool  _enabled = true;
    [ObservableProperty] private float _gainDb;
    [ObservableProperty] private float _pan;

    public StemChannelViewModel(StemType type)
    {
        Type = type;
    }
}
