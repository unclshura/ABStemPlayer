using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioCore.Impl;

public sealed class RubberBandTimeStretchEngine : ITimeStretchEngine, IDisposable
{
    private readonly AudioBufferPool _pool;
    private readonly int _sampleRate;
    private readonly int _channels;

    private FfmpegProcess? _ff;
    private Stream? _stdin;
    private Stream? _stdout;

    private float _speed = 1.0f;

    private readonly byte[] _ring;
    private int _ringWrite;
    private int _ringRead;
    private readonly object _ringLock = new();

    private Thread? _readerThread;
    private bool _readerRunning;

    public RubberBandTimeStretchEngine(AudioBufferPool pool, int sampleRate = 44100, int channels = 2)
    {
        _pool = pool;
        _sampleRate = sampleRate;
        _channels = channels;

        var bytesPerSecond = sampleRate * channels * sizeof(float);
        _ring = new byte[bytesPerSecond];

        StartProcess();
    }

    public void Configure(PlaybackSpeedSettings settings)
    {
        if (Math.Abs(settings.Speed - _speed) < 0.0001f)
            return;

        _speed = settings.Speed;
        RestartProcess();
    }

    public TimeStretchedAudioBlock Process(MixedAudioBlock input)
    {
        var expectedFloats = input.Frames * _channels;
        var expectedBytes  = expectedFloats * sizeof(float);

        if (Math.Abs(_speed - 1.0f) < 0.01f)
        {
            var buf = _pool.Rent(expectedFloats);
            Array.Copy(input.Buffer.Samples, buf.Samples, input.Buffer.Length);
            return new TimeStretchedAudioBlock(buf, input.Frames, _channels, _sampleRate);
        }

        var span  = input.Buffer.Span;
        var bytes = MemoryMarshal.AsBytes(span);

        _stdin!.Write(bytes);
        _stdin.Flush();

        var available = WaitForOutput();
        if (available <= 0)
            return default;

        var outBuf   = _pool.Rent(expectedFloats);
        var outBytes = MemoryMarshal.AsBytes(outBuf.Span);

        var readBytes = DrainRing(outBytes, expectedBytes);
        if (readBytes <= 0)
        {
            outBuf.Dispose();
            return default;
        }

        var frames = readBytes / (_channels * sizeof(float));
        outBuf.Length = frames * _channels;

        return new TimeStretchedAudioBlock(outBuf, frames, _channels, _sampleRate);
    }

    private int WaitForOutput(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
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

        Debug.WriteLine("Rubberband: Timeout waiting for output from ffmpeg");
        return 0;
    }

    private void StartProcess()
    {
        var cmd =
            $"-hide_banner -loglevel error " +
            $"-f f32le -ar {_sampleRate} -ac {_channels} -i pipe:0 " +
            $"-af \"rubberband=tempo={_speed}\" " +
            $"-f f32le -ar {_sampleRate} -ac {_channels} pipe:1";

        _ff = new FfmpegProcess(
            name: $"rubberband:{_speed:F3}",
            commandLine: cmd,
            redirectOutput: true,
            redirectInput: true);

        _ff.StartProcess();

        _stdin = _ff.Stdin!;
        _stdout = _ff.Stdout!;

        _readerRunning = true;
        _readerThread = new Thread(ReaderLoop) { IsBackground = true };
        _readerThread.Start();
    }

    private void RestartProcess()
    {
        DisposeProcess();
        ResetRing();
        StartProcess();
    }

    private void ReaderLoop()
    {
        var buf = new byte[4096];

        try
        {
            while (_readerRunning)
            {
                var read = _stdout!.Read(buf, 0, buf.Length);
                if (read <= 0)
                    break;

                lock (_ringLock)
                {
                    var first = Math.Min(read, _ring.Length - _ringWrite);
                    Buffer.BlockCopy(buf, 0, _ring, _ringWrite, first);
                    _ringWrite = (_ringWrite + first) % _ring.Length;

                    var remaining = read - first;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buf, first, _ring, _ringWrite, remaining);
                        _ringWrite = (_ringWrite + remaining) % _ring.Length;
                    }
                }
            }
        }
        catch { }
    }

    private int DrainRing(Span<byte> dest, int maxBytes)
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

    private void ResetRing()
    {
        lock (_ringLock)
        {
            _ringWrite = 0;
            _ringRead = 0;
        }
    }

    private void DisposeProcess()
    {
        _readerRunning = false;

        try { _stdout?.Close(); } catch { }
        try { _stdin?.Close(); } catch { }
        try { _ff?.Dispose(); } catch { }

        if (_readerThread != null)
        {
            try { _readerThread.Join(500); } catch { }
            _readerThread = null;
        }

        _ff = null;
        _stdin = null;
        _stdout = null;
    }

    public void Dispose() => DisposeProcess();
}
