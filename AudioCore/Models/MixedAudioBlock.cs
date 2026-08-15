using AudioCore.Impl;

namespace AudioCore.Models;

public readonly struct MixedAudioBlock : IAudioBlock, IDisposable
{
    public AudioBuffer<float> Buffer         { get; }
    public int         Frames         { get; }
    public int         Channels       { get; }
    public int         SampleRate     { get; }
    public long        Position       { get; }

    public Span<float> Span => Buffer.Span;
    public int Length => Buffer.Length;
    public MixedAudioBlock(AudioBuffer<float> buffer, int frames, int channels, int sampleRate, long samplePosition)
    {
        Buffer     = buffer;
        Frames     = frames;
        Channels   = channels;
        SampleRate = sampleRate;
        Position   = samplePosition;
    }

    public void Dispose() => Buffer.Dispose();
}
