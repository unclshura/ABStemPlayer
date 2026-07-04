using System.Buffers;

namespace AudioCore.Impl;

public class GenericBufferPool<T>
{
    private readonly ArrayPool<T> _pool = ArrayPool<T>.Shared;

    public AudioBuffer<T> Rent(int sampleCount)
    {
        var array = _pool.Rent(sampleCount);
        return new AudioBuffer<T>(array, sampleCount, this);
    }

    internal void Return(T[] array)
    {
        _pool.Return(array);
    }
}

public sealed class AudioBufferPool : GenericBufferPool<float> { }
public sealed class ByteBufferPool : GenericBufferPool<byte> { }
