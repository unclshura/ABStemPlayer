namespace AudioCore.Interfaces;

public interface IAudioReader : IDisposable
{
    int SampleRate    { get; }
    int Channels      { get; }
    long TotalSamples { get; }

    // Read PCM float samples into the provided buffer.
    // Returns number of samples actually read.
    Task<int> ReadAsync(Memory<float> buffer, CancellationToken token);

    // Seek to absolute sample index.
    void Seek(long sampleIndex);

    void Reset();
}
