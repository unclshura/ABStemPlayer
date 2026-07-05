namespace AudioCore.Interfaces;

public interface IStemPlaybackEngine
{
    PlaybackSession? CurrentSession { get; }

    Task LoadSessionAsync(PlaybackSession session, IProgressReporter<TimeSpan> progressReporter);

    // Transport
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);

    // Loop
    void SetLoop(TimeSpan start, TimeSpan end);
    void ClearLoop();
}
