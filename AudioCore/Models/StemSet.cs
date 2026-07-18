namespace AudioCore.Models;

public sealed class StemSet
{
    public string OriginalFilePath { get; init; } = string.Empty;
    public IReadOnlyList<StemTrack> Stems { get; init; } = Array.Empty<StemTrack>();

    public long TotalFrames => Stems.Max(s => s.TotalFrames);
}
