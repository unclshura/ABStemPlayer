namespace ABStemPlayer.ViewModels;

public partial class StemChannelViewModel : ObservableObject
{
    public StemType Type { get; }

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private float _gainDb = 0f;
    [ObservableProperty] private float _pan    = 0f;

    public StemChannelViewModel(StemType type)
    {
        Type = type;
    }
}
