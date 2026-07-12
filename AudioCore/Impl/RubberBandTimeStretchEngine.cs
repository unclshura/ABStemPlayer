using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioCore.Impl;

public sealed class RubberBandTimeStretchEngine : ITimeStretchEngine, IDisposable
{
    private readonly AudioBufferPool _pool;
    private readonly int             _sampleRate;
    private readonly int             _channels;

    private FfmpegProcess?           _ff;
    private Stream?                  _stdin;
    private Stream?                  _stdout;

    private BlockingRingBuffer       _ring;
    private float                    _speed = 1.0f;

    private Thread?                  _readerThread;
    private bool                     _readerRunning;

    public RubberBandTimeStretchEngine(AudioBufferPool pool, int sampleRate = 44100, int channels = 2)
    {
        _pool              = pool;
        _sampleRate        = sampleRate;
        _channels          = channels;

        var bytesPerSecond = sampleRate * channels * sizeof(float);
        _ring              = new BlockingRingBuffer(10 * bytesPerSecond);
    }

    public void Configure(PlaybackSpeedSettings settings)
    {
        if (Math.Abs(settings.Speed - _speed) < 0.0001f)
            return;

        _speed = settings.Speed;

        DisposeProcess();
        _ring.ResetRing();
    }

    public Task Submit(MixedAudioBlock input, CancellationToken token)
    {
        // No-stretch path: enqueue block and signal semaphore
        if (Math.Abs(_speed - 1.0f) < 0.01f)
        {
            var b = MemoryMarshal.AsBytes(input.Buffer.Span);
            _ring.WriteToOutput(b, b.Length, token);

            return Task.CompletedTask;
        }

        if (_ff is null)
            StartProcess();

        var span  = input.Buffer.Span;
        var bytes = MemoryMarshal.AsBytes(span);

        _stdin!.Write(bytes);
        _stdin.Flush();

        return Task.CompletedTask;
    }

    public async Task<TimeStretchedAudioBlock> Receive(CancellationToken token)
    {
        int available = 0;
        while (!token.IsCancellationRequested)
        {
            available = _ring.WaitForOutput(token);
            if (available > 0)
                break;

            await Task.Delay(2).ConfigureAwait(false);
        }

        if (token.IsCancellationRequested)
            return default;

        var maxFloats = available / sizeof(float);
        var outBuf    = _pool.Rent(maxFloats);
        var outBytes  = MemoryMarshal.AsBytes(outBuf.Span);

        var readBytes = _ring.DrainRing(outBytes, outBytes.Length);
        if (readBytes <= 0)
        {
            outBuf.Dispose();
            Debug.WriteLine("RubberBandTimeStretchEngine: Failed to drain ring buffer.");
            return default;
        }

        var frames = readBytes / (_channels * sizeof(float));
        outBuf.Length = frames * _channels;

        return new TimeStretchedAudioBlock(outBuf, frames, _channels, _sampleRate);
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

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _ring.WriteToOutput(buf, read, cts.Token);
            }
        }
        catch { }
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

    public void Dispose()
    {
        DisposeProcess();
    }
}
