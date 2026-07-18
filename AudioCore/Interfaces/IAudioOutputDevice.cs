using NAudio.Wave;

namespace AudioCore.Interfaces;

public interface IAudioOutputDevice
{
    int SampleRate { get; }
    int Channels { get; }

    Task IsReadyToAccept(CancellationToken token);

    void Start();
    void Stop();
    void Pause();
    PlaybackState State { get; }

    // Push interleaved float32 PCM
    void Write(ReadOnlySpan<float> samples);
}
