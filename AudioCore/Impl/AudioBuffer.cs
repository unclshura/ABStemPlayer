namespace AudioCore.Impl;

public class AudioBuffer<T> : IDisposable
{
    private readonly GenericBufferPool<T> _owner;
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
}
