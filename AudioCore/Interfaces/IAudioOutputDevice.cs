namespace AudioCore.Interfaces;

public interface IAudioOutputDevice
{
    int SampleRate { get; }
    int Channels { get; }

    void Start();
    void Stop();

    // Push interleaved float32 PCM
    void Write(ReadOnlySpan<float> samples);
}
