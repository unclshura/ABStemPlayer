namespace AudioCore.Models;

public sealed class PlaybackSession
{
    public StemSet StemSet { get; init; } = default!;
    public long TotalFrames => StemSet?.TotalFrames ?? 0;
    public MixerSettings Mixer { get; set; } = new() { Stems = [] };
    public LoopRegion Loop { get; set; } = new();
    public PlaybackSpeedSettings Speed { get; set; } = new();
}
