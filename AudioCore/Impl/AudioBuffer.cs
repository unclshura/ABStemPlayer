using System.Runtime.InteropServices;

namespace AudioCore.Impl;

public class AudioBuffer<T> : IDisposable where T : unmanaged
{
    private readonly GenericBufferPool<T> _owner;
    private static GenericBufferPool<byte> _bytePool = new GenericBufferPool<byte>();
    private bool _disposed;

    public T[] Samples { get; }
    public int Length { get; set; } // number of valid samples

    internal AudioBuffer(T[] samples, int capacity, GenericBufferPool<T> owner)
    {
        Samples = samples;
        Length = capacity;
        _owner = owner;
    }

    public Span<T> Span => _disposed ? default : Samples.AsSpan(0, Length);

    public void Dispose()
    {
        if (_disposed) 
            return;
        _owner.Return(Samples);
        _disposed = true;
    }

    public async Task WriteAsync(Stream stream, CancellationToken token)
    {
        using var outBuf = _bytePool.Rent(Samples.Length * sizeof(T));

        var src = Samples.AsSpan(0, Length);
        var dst = outBuf.Span;

        MemoryMarshal.Cast<T, byte>(src).CopyTo(dst);

        await stream.WriteAsync(outBuf.Samples, 0, dst.Length, token)
                    .ConfigureAwait(false);
    }


}
