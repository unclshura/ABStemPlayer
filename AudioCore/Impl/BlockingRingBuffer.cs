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
        _ring      = new byte[size];
        _ringWrite = 0;
        _ringRead  = 0;
    }

    public void Write(ReadOnlySpan<byte> src, int srcLen, CancellationToken ct)
    {
        int written = 0;

        while (written < srcLen)
        {
            if ( ct.IsCancellationRequested )
            {
                Debug.WriteLine("BlockingRingBuffer: Write: operation cancelled.");
                return;
            }

            int remaining = srcLen - written;

            int free;
            lock (_ringLock)
            {
                int used = (_ringWrite >= _ringRead)
                ? _ringWrite - _ringRead
                : _ring.Length - _ringRead + _ringWrite;

                free = _ring.Length - used - 1;
            }

            if (free <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int toWrite = Math.Min(remaining, free);

            lock (_ringLock)
            {
                int first = Math.Min(toWrite, _ring.Length - _ringWrite);

                src.Slice(written, first)
                   .CopyTo(new Span<byte>(_ring, _ringWrite, first));

                _ringWrite = (_ringWrite + first) % _ring.Length;

                int leftover = toWrite - first;
                if (leftover > 0)
                {
                    src.Slice(written + first, leftover)
                       .CopyTo(new Span<byte>(_ring, _ringWrite, leftover));

                    _ringWrite = (_ringWrite + leftover) % _ring.Length;
                }
            }

            written += toWrite;
        }
    }

    public async Task<int> WaitForRoomToWrite(CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested)
            {
                Debug.WriteLine("BlockingRingBuffer: WaitForRoomToWrite: operation cancelled.");
                return 0;
            }

            lock (_ringLock)
            {
                var used = (_ringWrite >= _ringRead)
                    ? _ringWrite - _ringRead
                    : _ring.Length - _ringRead + _ringWrite;

                var free = _ring.Length - used - 1;

                if (free > 0)
                    return free;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }
    }
    public async Task<int> WaitForDataToRead(CancellationToken token)
    {
        while (true)
        {
            if (token.IsCancellationRequested)
            {
                Debug.WriteLine("BlockingRingBuffer: WaitForDataToRead: operation cancelled.");
                return 0;
            }

            lock (_ringLock)
            {
                var used = (_ringWrite >= _ringRead)
                    ? _ringWrite - _ringRead
                    : _ring.Length - _ringRead + _ringWrite;

                if (used > 0)
                    return used;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }
    }

    public int Read(Span<byte> dest, int maxBytes)
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

    public void Reset()
    {
        lock (_ringLock)
        {
            _ringWrite = 0;
            _ringRead = 0;
        }
    }

}
