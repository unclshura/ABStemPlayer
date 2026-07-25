namespace AudioCore.Interfaces;

public sealed class PlaybackSpeedSettings
{
    public float Speed { get; set; } = 1.0f; // 0.5x, 1.0x, 1.5x, etc.

    public override string ToString() => $"Speed: {Speed:N2}";
}


public interface ITimeStretchEngine
{
    Task Configure(PlaybackSpeedSettings settings, CancellationToken token);

    // Streaming block processing
    Task IsReadyToAcceptStems(CancellationToken token);
    Task SubmitStems(IReadOnlyList<AudioBlock> stemBlocks, CancellationToken token);
    Task<TimeStretchedAudioBlock[]> ReceiveStems(CancellationToken token);
}
