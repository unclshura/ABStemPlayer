namespace AudioCore.Interfaces;

public interface IStemDecoder : IDisposable
{
    StemTrack Stem { get; }

    Task<AudioBlock?> DecodeNextBlockAsync(CancellationToken token);

    void Seek(long samplePosition);

    void Reset();
}
