using Avalonia.Controls;

namespace ABStemPlayer.Views;

public partial class WaveformBandView : UserControl
{
    public WaveformBandView()
    {
        InitializeComponent();
    }

    private void WaveformCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is WaveformBandViewModel vm)
        {
            vm.UpdateBarsForCanvasSize(e.NewSize.Width, e.NewSize.Height);
        }
    }

}
