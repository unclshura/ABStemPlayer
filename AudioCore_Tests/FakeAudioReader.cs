using AudioCore.Interfaces;

namespace AudioCore_Tests;

public sealed class FakeAudioReader : IAudioReader
{
    private readonly float[] _data;
    private long _pos;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalSamples => _data.Length;

    public FakeAudioReader(float[] data, int sampleRate = 48000, int channels = 2)
    {
        _data = data;
        SampleRate = sampleRate;
        Channels = channels;
        _pos = 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FakeAudioReader));

        var remaining = _data.Length - _pos;
        if (remaining <= 0)
            return 0;

        var toRead = (int)Math.Min(count, remaining);
        Array.Copy(_data, _pos, buffer, offset, toRead);
        _pos += toRead;
        return toRead;
    }

    public void Seek(long samplePosition)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FakeAudioReader));

        _pos = Math.Clamp(samplePosition * Channels, 0, _data.Length);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public void Reset() => _pos = 0;
}
