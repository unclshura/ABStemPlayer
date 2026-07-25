using AudioCore.Impl;

namespace AudioCore.Models;

public readonly struct TimeStretchedAudioBlock : IDisposable
{
    public AudioBuffer<float> Buffer { get; }
    public int Frames     { get; }
    public int Channels   { get; }
    public int SampleRate { get; }
    public long Position { get; }

    public TimeStretchedAudioBlock(AudioBuffer<float> buffer, int frames, int channels, int sampleRate, long position)
    {
        Buffer     = buffer;
        Frames     = frames;
        Channels   = channels;
        SampleRate = sampleRate;
        Position   = position;
    }

    public void Dispose() => Buffer?.Dispose();
    public override string ToString() => $"{Frames} x {Channels} = {Buffer.Length} @ {Position}";
}
