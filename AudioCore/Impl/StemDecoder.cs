using AudioCore.Impl;

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
        _reader = reader;
        _pool = pool;
        _blockSize = blockSize;
        Stem = stem;

        Stem.Channels = reader.Channels;
        Stem.SampleRate = reader.SampleRate;
        Stem.Duration = TimeSpan.FromSeconds((double)reader.TotalSamples / reader.SampleRate);
    }

    public async Task<AudioBlock?> DecodeNextBlockAsync(CancellationToken token)
    {
        int channels     = _reader.Channels;
        int floatsNeeded = _blockSize * channels;

        var buf = _pool.Rent(floatsNeeded);

        // Async read into Memory<float>
        int readFloats = await _reader.ReadAsync(buf.Samples.AsMemory(0, floatsNeeded), token)
                                      .ConfigureAwait(false);

        if (readFloats <= 0)
        {
            buf.Dispose();
            return null;
        }

        buf.Length = readFloats;

        long pos = _currentSample;
        _currentSample += readFloats / channels;

        return new AudioBlock(buf, _reader.SampleRate, channels, pos);
    }

    public void Seek(long samplePosition)
    {
        _reader.Seek(samplePosition);
        _currentSample = samplePosition;
    }

    public void Reset() => Seek(0);

    public void Dispose() => _reader.Dispose();
}
