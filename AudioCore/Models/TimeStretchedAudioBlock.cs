using AudioCore.Impl;

namespace AudioCore.Models;

public readonly struct TimeStretchedAudioBlock : IDisposable
{
    public AudioBuffer<float> Buffer { get; }
    public int Frames     { get; }
    public int Channels   { get; }
    public int SampleRate { get; }

    public TimeStretchedAudioBlock(AudioBuffer<float> buffer, int frames, int channels, int sampleRate)
    {
        Buffer     = buffer;
        Frames     = frames;
        Channels   = channels;
        SampleRate = sampleRate;
    }

    public void Dispose() => Buffer?.Dispose();
}
