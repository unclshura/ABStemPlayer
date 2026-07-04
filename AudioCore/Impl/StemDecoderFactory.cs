namespace AudioCore.Impl;

public sealed class StemDecoderFactory : IStemDecoderFactory
{
    private readonly IAudioReaderFactory _readerFactory;
    private readonly AudioBufferPool _pool;
    private readonly int _blockSize;

    public StemDecoderFactory(
        IAudioReaderFactory readerFactory,
        AudioBufferPool pool,
        int blockSize = 4096)
    {
        _readerFactory = readerFactory;
        _pool = pool;
        _blockSize = blockSize;
    }

    public IStemDecoder Create(StemTrack stem)
    {
        var reader = _readerFactory.Create(stem.FilePath);
        return new StemDecoder(reader, _pool, stem, _blockSize);
    }
}
