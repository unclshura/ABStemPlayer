using System.Diagnostics;

namespace AudioCore.Impl;

public class BlockingRingBuffer
{
    private readonly byte[]          _ring;
    private int                      _ringWrite;
    private int                      _ringRead;
    private readonly object          _ringLock = new();

    public BlockingRingBuffer(int size)
    {
        _ring = new byte[size];
        _ringWrite = 0;
        _ringRead = 0;
    }

    public void WriteToOutput(ReadOnlySpan<byte> src, int srcLen, CancellationToken ct)
    {
        int written = 0;

        while (written < srcLen)
        {
            ct.ThrowIfCancellationRequested();

            int remaining = srcLen - written;

            int free;
            lock (_ringLock)
            {
                int used = (_ringWrite >= _ringRead)
                ? _ringWrite - _ringRead
                : _ring.Length - _ringRead + _ringWrite;

                free = _ring.Length - used - 1; // leave 1 byte to distinguish full/empty
            }

            if (free <= 0)
            {
                // No room → block until space becomes available
                Thread.Sleep(1);
                continue;
            }

            int toWrite = Math.Min(remaining, free);

            lock (_ringLock)
            {
                int first = Math.Min(toWrite, _ring.Length - _ringWrite);

                // Write first segment
                src.Slice(written, first)
                   .CopyTo(new Span<byte>(_ring, _ringWrite, first));

                _ringWrite = (_ringWrite + first) % _ring.Length;

                int leftover = toWrite - first;
                if (leftover > 0)
                {
                    // Wrap-around segment
                    src.Slice(written + first, leftover)
                       .CopyTo(new Span<byte>(_ring, _ringWrite, leftover));

                    _ringWrite = (_ringWrite + leftover) % _ring.Length;
                }
            }

            written += toWrite;
        }
    }

    public int WaitForOutput(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_ringLock)
            {
                var available = (_ringWrite >= _ringRead)
                    ? _ringWrite - _ringRead
                    : _ring.Length - _ringRead + _ringWrite;

                if (available > 0)
                    return available;
            }

            Thread.Sleep(2);
        }

        Debug.WriteLine("BlockingRingBuffer: Timeout waiting for output");
        return 0;
    }

    public int DrainRing(Span<byte> dest, int maxBytes)
    {
        lock (_ringLock)
        {
            var available = (_ringWrite >= _ringRead)
                ? _ringWrite - _ringRead
                : _ring.Length - _ringRead + _ringWrite;

            if (available <= 0)
                return 0;

            var toRead = Math.Min(available, Math.Min(maxBytes, dest.Length));

            var first = Math.Min(toRead, _ring.Length - _ringRead);
            new Span<byte>(_ring, _ringRead, first).CopyTo(dest.Slice(0, first));
            _ringRead = (_ringRead + first) % _ring.Length;

            var remaining = toRead - first;
            if (remaining > 0)
            {
                new Span<byte>(_ring, _ringRead, remaining)
                    .CopyTo(dest.Slice(first, remaining));
                _ringRead = (_ringRead + remaining) % _ring.Length;
            }

            return toRead;
        }
    }

    public void ResetRing()
    {
        lock (_ringLock)
        {
            _ringWrite = 0;
            _ringRead = 0;
        }
    }

}
