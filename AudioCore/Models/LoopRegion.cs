namespace AudioCore.Models;

public sealed class LoopRegion
{
    public bool IsEnabled { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
}
