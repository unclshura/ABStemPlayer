using AudioCore.Impl;

namespace AudioCore.Models;

public readonly struct MixedAudioBlock : IDisposable
{
    public AudioBuffer<float> Buffer         { get; }
    public int         Frames         { get; }
    public int         Channels       { get; }
    public int         SampleRate     { get; }
    public long        SamplePosition { get; }

    public MixedAudioBlock(AudioBuffer<float> buffer, int frames, int channels, int sampleRate, long samplePosition)
    {
        Buffer         = buffer;
        Frames         = frames;
        Channels       = channels;
        SampleRate     = sampleRate;
        SamplePosition = samplePosition;
    }

    public void Dispose() => Buffer.Dispose();
}
