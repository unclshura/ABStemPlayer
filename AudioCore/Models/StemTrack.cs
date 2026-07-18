namespace AudioCore.Models;

public sealed class StemTrack
{
    public StemType Type     { get; init; }
    public string Name       { get; init; } = string.Empty;
    public string FilePath   { get; init; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public int SampleRate    { get; set; }
    public int Channels      { get; set; }
    public float[] Waveform  { get; set; } = [];

    public long TotalFrames => (long)(Duration.TotalMilliseconds * SampleRate / 1000.0);
}
