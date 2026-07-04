using System.Collections.ObjectModel;

namespace ABStemPlayer.ViewModels;

public sealed class MixerViewModel
{
    public ObservableCollection<StemChannelViewModel> Stems { get; } = new();

    public MixerViewModel()
    {
        foreach (var type in Enum.GetValues<StemType>())
        {
            Stems.Add(new StemChannelViewModel(type));
        }
    }

}
