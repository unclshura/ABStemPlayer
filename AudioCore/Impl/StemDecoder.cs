namespace AudioCore.Impl;

public sealed class StemDecoder : IStemDecoder
{
    private readonly IAudioReader _reader;
    private readonly AudioBufferPool _pool;
    private readonly int _blockSize;
    private long _currentSample;

    public StemTrack Stem { get; }

    public StemDecoder(
        IAudioReader reader,
        AudioBufferPool pool,
        StemTrack stem,
        int blockSize = 4096)
    {
        _reader    = reader;
        _pool      = pool;
        _blockSize = blockSize;
        Stem       = stem;

        Stem.Channels   = reader.Channels;
        Stem.SampleRate = reader.SampleRate;
        Stem.Duration   = TimeSpan.FromSeconds((double)reader.TotalSamples / reader.SampleRate);
    }

    public bool TryDecodeNextBlock(out AudioBlock block)
    {
        var channels = _reader.Channels;
        var floatsNeeded = _blockSize * channels;

        var buf = _pool.Rent(floatsNeeded);
        var readFloats = _reader.Read(buf.Samples, 0, floatsNeeded);

        if (readFloats <= 0)
        {
            buf.Dispose();
            block = default;
            return false;
        }

        buf.Length = readFloats;

        var pos = _currentSample;
        _currentSample += readFloats / channels;

        block = new AudioBlock(buf, _reader.SampleRate, channels, pos);
        return true;
    }

    public void Seek(long samplePosition)
    {
        _reader.Seek(samplePosition);
        _currentSample = samplePosition;
    }

    public void Reset() => Seek(0);

    public void Dispose() => _reader.Dispose();
}
