using System.Diagnostics;
using System.Runtime.InteropServices;
using static AudioCore.Models.Tracer;

namespace AudioCore.Impl;

public sealed class RubberBandTimeStretchEngine : ITimeStretchEngine, IAsyncDisposable
{
    private readonly AudioBufferPool _pool;
    private readonly int             _sampleRate;
    private long[] _sourcePositions;

    // One RubberBand/ffmpeg process per stem (each is stereo: 2 channels)
    private sealed class StemProcess : IDisposable
    {
        public readonly int StemIndex;
        public FfmpegProcess? Ff;
        public Stream?        Stdin;
        public Stream?        Stdout;
        public BlockingRingBuffer Ring;

        public StemProcess(int stemIndex, int sampleRate)
        {
            StemIndex = stemIndex;
            // 2 channels per stem
            var bytesPerSecond = sampleRate * 2 * sizeof(float);
            Ring = new BlockingRingBuffer(bytesPerSecond * 2);
        }

        public void Dispose()
        {
            try { Stdout?.Close(); } catch { }
            try { Stdin?.Close();  } catch { }
            try { Ff?.Dispose();   } catch { }
            Ring.Reset();
        }
    }

    private readonly List<StemProcess> _stemProcesses = new();
    private readonly int _stemCount;

    private float                    _speed = 1.0f;
    private CancellationTokenSource? _cts;
    private CancellationToken        _token;
    private Task?                    _readerTask;

    public RubberBandTimeStretchEngine(AudioBufferPool pool, int sampleRate = 44100, int stemCount = 6)
    {
        _pool                   = pool;
        _sampleRate             = sampleRate;
        _stemProcesses.Capacity = stemCount;
        _stemCount              = stemCount;
        _sourcePositions = new long[_stemCount];
    }

    public async Task Configure(PlaybackSpeedSettings settings, CancellationToken token)
    {
        Trace(settings);
        _speed = settings.Speed;

        if (_cts != null)
            await DisposeProcesses().ConfigureAwait(false);

        if ( token != CancellationToken.None )
            _token = token;
    }


    public Task IsReadyToAcceptStems(CancellationToken token)
    {
        EnsureStemProcesses(_stemCount);
        // Wait until all rings have room (simple check: any one is fine for now)
        return Task.CompletedTask;
    }

    public Task SubmitStems(IReadOnlyList<AudioBlock> stemBlocks, CancellationToken token)
    {
        if ( stemBlocks.Count != _stemCount)
            throw new ArgumentException($"Expected {_stemCount} stems, but got {stemBlocks.Count}.");

        // No-stretch path: just enqueue into per-stem rings
        if (Math.Abs(_speed - 1.0f) < 0.01f)
        {
            EnsureStemProcesses(stemBlocks.Count);

            for (int i = 0; i < stemBlocks.Count; i++)
            {
                var proc = _stemProcesses[i];
                var bytes = MemoryMarshal.AsBytes(stemBlocks[i].Buffer.Span);
                proc.Ring.Write(bytes, bytes.Length, token);
            }

            return Task.CompletedTask;
        }

        // Stretch path: one ffmpeg+rubberband per stem
        EnsureStemProcesses(stemBlocks.Count);
        StartProcessesIfNeeded(stemBlocks.Count);

        for (int i = 0; i < stemBlocks.Count; i++)
        {
            var proc  = _stemProcesses[i];
            var span  = stemBlocks[i].Buffer.Span;
            var bytes = MemoryMarshal.AsBytes(span);

            try
            {
                if (token.IsCancellationRequested)
                    return Task.CompletedTask;

                if (proc.Stdin != null && !(proc.Ff?.Proc?.HasExited ?? true))
                    proc.Stdin.Write(bytes);

                if (token.IsCancellationRequested)
                    return Task.CompletedTask;
                try
                {
                    proc.Stdin.Flush();
                }
                catch (System.ObjectDisposedException)
                {
                    // process has exited
                }
            }
            catch
            {
                // ignore
            }
        }

        return Task.CompletedTask;
    }

    public async Task<TimeStretchedAudioBlock[]> ReceiveStems(CancellationToken token)
    {
        EnsureStemProcesses(_stemCount);

        int framesPerBlock   = (int)(_sampleRate / 2);     // 0.5 seconds
        int samplesPerBlock  = framesPerBlock * 2;         // stereo
        int bytesPerBlock    = samplesPerBlock * sizeof(float);

        var result = new TimeStretchedAudioBlock[_stemCount];

        for (int i = 0; i < _stemCount; i++)
        {
            var proc = _stemProcesses[i];

            // Wait until *some* data is available
            int available = 0;
            while (!token.IsCancellationRequested)
            {
                available = await proc.Ring.WaitForDataToRead(token).ConfigureAwait(false);
                if (available > 0)
                    break;

                await Task.Delay(1, token).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
                return Array.Empty<TimeStretchedAudioBlock>();

            // Determine block size (final block may be smaller)
            int bytesToRead = Math.Min(bytesPerBlock, available);
            int samplesToRead = bytesToRead / sizeof(float);
            int framesToRead  = samplesToRead / 2;

            // Allocate a temporary byte[] buffer (safe across await)
            byte[] temp = new byte[bytesToRead];

            int totalRead = 0;

            // Read exactly bytesToRead into temp[]
            while (totalRead < bytesToRead && !token.IsCancellationRequested)
            {
                int toRead = bytesToRead - totalRead;
                int read   = proc.Ring.Read(temp.AsSpan(totalRead, toRead), toRead);

                if (read > 0)
                {
                    totalRead += read;
                    continue;
                }

                await Task.Delay(1, token).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested)
                return Array.Empty<TimeStretchedAudioBlock>();

            // Now allocate the float buffer
            var outBuf = _pool.Rent(samplesToRead);
            outBuf.Length = samplesToRead;

            // Copy temp[] → float buffer (safe, no await)
            var outBytes = MemoryMarshal.AsBytes(outBuf.Span);
            temp.AsSpan().CopyTo(outBytes);

            // Compute source position
            long sourceFrames = (long)(framesToRead * _speed);
            long sourcePos    = _sourcePositions[i];
            _sourcePositions[i] += sourceFrames;

            result[i] = new TimeStretchedAudioBlock(
                outBuf,
                framesToRead,
                2,
                _sampleRate,
                sourcePos);
        }

        return result;
    }


    private void EnsureStemProcesses(int stemCount)
    {
        while (_stemProcesses.Count < stemCount)
            _stemProcesses.Add(new StemProcess(_stemProcesses.Count, _sampleRate));
    }

    private void StartProcessesIfNeeded(int stemCount)
    {
        if (_cts != null)
            return;

        Msg("Starting RubberBand/ffmpeg processes for {stemCount} stems at speed {_speed:F2}...");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(_token);

        for (int i = 0; i < stemCount; i++)
        {
            var proc = _stemProcesses[i];
            if (proc.Ff != null)
                continue;

            var cmd =
                "-hide_banner -loglevel error " +
                $"-f f32le -ar {_sampleRate} -ac 2 -i pipe:0 " +
                $"-af \"rubberband=tempo={_speed}\" " +
                $"-f f32le -ar {_sampleRate} -ac 2 pipe:1";

            proc.Ff = new FfmpegProcess(
                name: $"rubberband:stem{i}:{_speed:F3}",
                commandLine: cmd,
                redirectOutput: true,
                redirectInput: true);

            proc.Ff.StartProcess();

            proc.Stdin = proc.Ff.Stdin!;
            proc.Stdout = proc.Ff.Stdout!;
        }

        _readerTask = Task.Run(() => ReaderLoop(_cts.Token));
    }

    private async Task ReaderLoop(CancellationToken token)
    {
        Trace();

        var buf = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool anyActive = false;

                foreach (var proc in _stemProcesses)
                {
                    if (proc.Stdout == null)
                        continue;

                    anyActive = true;

                    var read = await proc.Stdout.ReadAsync(buf, 0, buf.Length, token).ConfigureAwait(false);
                    if (read > 0)
                        proc.Ring.Write(buf, read, token);
                }

                if (!anyActive)
                    break;
            }
        }
        catch { }
    }

    private async Task DisposeProcesses()
    {
        if (_cts != null)
        {
            Msg("Cancelling RubberBand/ffmpeg reader task...");
            try { _cts.Cancel(); } catch { }
        }

        if (_readerTask != null)
        {
            Msg("Waiting for RubberBand/ffmpeg reader task to complete...");
            try { await _readerTask.ConfigureAwait(false); } catch { }
            _readerTask = null;
        }

        if (_stemProcesses.Count > 0)
        {
            Msg("Disposing RubberBand/ffmpeg processes...");
            foreach (var proc in _stemProcesses)
                proc.Dispose();

            _stemProcesses.Clear();
        }

        if (_cts != null)
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Trace();
        await DisposeProcesses().ConfigureAwait(false);
    }
}
