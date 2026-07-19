using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioCore.Impl;

public sealed class RubberBandTimeStretchEngine : ITimeStretchEngine, IAsyncDisposable
{
    private readonly AudioBufferPool _pool;
    private readonly int             _sampleRate;
    private readonly int             _channels;

    private FfmpegProcess?           _ff;
    private Stream?                  _stdin;
    private Stream?                  _stdout;

    private BlockingRingBuffer       _ring;
    private float                    _speed = 1.0f;

    private Task?                    _readerTask;
    private CancellationTokenSource? _cts;
    private CancellationToken        _token;

    public RubberBandTimeStretchEngine(AudioBufferPool pool, int sampleRate = 44100, int channels = 2)
    {
        _pool              = pool;
        _sampleRate        = sampleRate;
        _channels          = channels;

        var bytesPerSecond = sampleRate * channels * sizeof(float);
        _ring              = new BlockingRingBuffer( bytesPerSecond * 2);
    }

    public async Task Configure(PlaybackSpeedSettings settings, CancellationToken token)
    {
        _speed = settings.Speed;
        
        if ( _cts != null && _ff != null )
            await DisposeProcess().ConfigureAwait(false);

        _ring.Reset();
        _token = token;
    }

    public Task IsReadyToAccept(CancellationToken token) => _ring.WaitForRoomToWrite(token);

    public Task Submit(MixedAudioBlock input, CancellationToken token)
    {
        // No-stretch path: enqueue block and signal semaphore
        if (Math.Abs(_speed - 1.0f) < 0.01f)
        {
            var b = MemoryMarshal.AsBytes(input.Buffer.Span);
            _ring.Write(b, b.Length, token);

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
            available = await _ring.WaitForDataToRead(token).ConfigureAwait(false);
            if (available > 0)
                break;

            await Task.Delay(2).ConfigureAwait(false);
        }

        if (token.IsCancellationRequested)
            return default;

        var maxFloats = available / sizeof(float);
        var outBuf    = _pool.Rent(maxFloats);
        var outBytes  = MemoryMarshal.AsBytes(outBuf.Span);

        var readBytes = _ring.Read(outBytes, outBytes.Length);
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

        Debug.Assert(_cts == null);

        _cts           = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _readerTask    = Task.Run(ReaderLoop);
    }

    private async Task ReaderLoop()
    {
        Debug.Assert(_cts != null);

        var buf = new byte[4096];

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var read = await _stdout!.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                _ring.Write(buf, read, _cts.Token);
            }
        }
        catch { }
    }


    private async Task DisposeProcess()
    {
        try { _stdout?.Close();   } catch { }
        try { _stdin ?.Close();   } catch { }
        try { _ff    ?.Dispose(); } catch { }

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_readerTask != null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch { }
            _readerTask = null;
        }

        _ff     = null;
        _stdin  = null;
        _stdout = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeProcess().ConfigureAwait(false);
    }
}
