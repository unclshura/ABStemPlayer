namespace ABStemPlayer.ViewModels;

public sealed class SegmentViewModel
{
    public string Name { get; set; } = string.Empty;

    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }

    public bool Active { get; set; }

    public string StartFormatted => Start.ToString("mm\\:ss");
    public string EndFormatted => End.ToString("mm\\:ss");

    // waveform rendering helpers
    public double StartX { get; set; }
    public double Width { get; set; }
    public double SegmentHeight { get; set; } = 80;
}
