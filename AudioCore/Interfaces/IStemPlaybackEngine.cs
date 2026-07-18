namespace AudioCore.Interfaces;

public interface IStemPlaybackEngine
{
    PlaybackSession? CurrentSession { get; }

    Task LoadSessionAsync(PlaybackSession session, IProgressReporter<double> progressReporter);

    // Transport
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);

    // Dynamic controls
    Task UpdatePlaybackSpeedAsync(PlaybackSpeedSettings settings);
    Task UpdateMixerAsync(MixerSettings settings);

    // Loop
    void SetLoop(TimeSpan start, TimeSpan end);
    void ClearLoop();
}
