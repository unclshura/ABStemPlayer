namespace AudioCore.Interfaces;

public interface IStemDecoder : IDisposable
{
    StemTrack Stem { get; }

    bool TryDecodeNextBlock(out AudioBlock block);

    void Seek(long samplePosition);

    void Reset();
}
