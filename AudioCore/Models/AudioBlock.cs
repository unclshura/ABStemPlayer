using AudioCore.Impl;

namespace AudioCore.Models;

public readonly struct AudioBlock : IDisposable
{
    public AudioBuffer<float> Buffer { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long Position { get; }
    public int Frames => Buffer.Length / Channels;

    public AudioBlock(AudioBuffer<float> buffer, int sampleRate, int channels, long samplePosition)
    {
        Buffer     = buffer;
        SampleRate = sampleRate;
        Channels   = channels;
        Position   = samplePosition;
    }

    public Span<float> Span => Buffer.Span;
    public int Length => Buffer.Length;

    public void Dispose() => Buffer.Dispose();
}
