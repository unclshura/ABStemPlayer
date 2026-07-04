using System.Collections.ObjectModel;
using Avalonia.Media;

namespace ABStemPlayer.ViewModels;

public partial class WaveformBandViewModel : ObservableObject
{
    public sealed class WaveformBar
    {
        public double X { get; set; }      // Canvas.Left
        public double Y { get; set; }      // Canvas.Top
        public double Width { get; set; }  // bar width
        public double Height { get; set; } // bar height

        public override string ToString() => Height.ToString();
    }


    private readonly StemTrack _stem;
    public string BandName => _stem.Name;

    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;
    [ObservableProperty] private Geometry? _waveformGeometry;

    public ObservableCollection<WaveformBar> WaveformBars { get; } = new();

    [ObservableProperty] private double _playbackX;

    public ObservableCollection<SegmentViewModel> Segments { get; } = new();

    public TimeSpan Duration { get; set; }
    public string DurationFormatted => Duration.ToString("mm\\:ss");

    public WaveformBandViewModel(StemTrack stem)
    {
        _stem = stem;
    }

    public void UpdatePlaybackPosition(TimeSpan current, TimeSpan total)
    {
        if (total <= TimeSpan.Zero || CanvasWidth <= 0)
        {
            PlaybackX = 0;
            return;
        }

        double ratio = current.TotalSeconds / total.TotalSeconds;
        PlaybackX = ratio * CanvasWidth;
    }

    public void UpdateBarsForCanvasSize(double canvasWidth, double canvasHeight)
    {
        CanvasWidth  = canvasWidth;
        CanvasHeight = canvasHeight;
        var waveform = _stem.Waveform;

        if (waveform == null || waveform.Length == 0)
        {
            WaveformGeometry = null;
            return;
        }

        var geo = new StreamGeometry();

        using (var ctx = geo.Open())
        {
            double half = canvasHeight / 2.0;
            double spacing = 1.0;
            double barWidth = (canvasWidth - (waveform.Length - 1) * spacing) / waveform.Length;

            double x = 0;

            for (int i = 0; i < waveform.Length; i++)
            {
                double amp = Math.Abs(waveform[i]);
                double h = amp * half;

                // draw vertical bar
                ctx.BeginFigure(new Point(x, half - h), false);
                ctx.LineTo(new Point(x, half + h));

                x += barWidth + spacing;
            }
        }

        WaveformGeometry = geo;
    }


}
