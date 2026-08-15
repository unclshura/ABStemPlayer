using AudioCore.Impl;

namespace AudioCore.Models;

public interface IAudioBlock : IDisposable
{
    AudioBuffer<float> Buffer { get; }
    int SampleRate { get; }
    int Channels { get; }
    long Position { get; }
    int Frames { get; }
    int Length { get; }
    Span<float> Span { get; }
}
