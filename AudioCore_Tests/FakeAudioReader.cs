using System.Buffers;
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

    public Task<int> ReadAsync(Memory<float> buffer, CancellationToken token)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FakeAudioReader));

        if (token.IsCancellationRequested)
            return Task.FromCanceled<int>(token);

        // How many floats remain?
        long remaining = _data.Length - _pos;
        if (remaining <= 0)
            return Task.FromResult(0);

        // How many floats can we copy?
        int toCopy = (int)Math.Min(buffer.Length, remaining);

        // Copy from backing array into caller's buffer
        _data.AsMemory((int)_pos, toCopy).CopyTo(buffer);

        // Advance position
        _pos += toCopy;

        return Task.FromResult(toCopy);
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
