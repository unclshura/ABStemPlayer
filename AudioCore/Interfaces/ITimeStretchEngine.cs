namespace AudioCore.Interfaces;

public sealed class PlaybackSpeedSettings
{
    public float Speed { get; set; } = 1.0f; // 0.5x, 1.0x, 1.5x, etc.
}


public interface ITimeStretchEngine
{
    void Configure(PlaybackSpeedSettings settings);

    // Streaming block processing
    TimeStretchedAudioBlock Process(MixedAudioBlock input);
}
