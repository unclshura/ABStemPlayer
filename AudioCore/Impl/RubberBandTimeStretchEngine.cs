using System.Runtime.InteropServices;
using static AudioCore.Models.Tracer;

namespace AudioCore.Impl;

public sealed class RubberBandTimeStretchEngine : ITimeStretchEngine, IAsyncDisposable
{
    private readonly AudioBufferPool _pool;
    private readonly int             _sampleRate;
    private readonly long[]          _sourcePositions;

    private sealed class SourceChunkInfo
    {
        public long SourcePosition;   // starting frame index in source stream
        public int  SourceFrames;     // number of frames in this chunk
    }

    private readonly Queue<SourceChunkInfo>[] _pendingInput;
    private readonly double[]                 _fractionalInput;
    private readonly List<StemProcess>        _stemProcesses = [];
    private readonly int                      _stemCount;
    private int                               _activeIo; // Interlocked counter
    private float                             _speed = 1.0f;
    private CancellationTokenSource?          _cts;
    private CancellationToken                 _token;
    private Task?                             _readerTask;

    // One RubberBand/ffmpeg process per stem (each is stereo: 2 channels)
    private sealed class StemProcess : IDisposable
    {
        public readonly int       StemIndex;
        public FfmpegProcess?     Ff;
        public Stream?            Stdin;
        public Stream?            Stdout;
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



    public RubberBandTimeStretchEngine(AudioBufferPool pool, int sampleRate = 44100, int stemCount = 6)
    {
        _pool                   = pool;
        _sampleRate             = sampleRate;
        _stemProcesses.Capacity = stemCount;
        _stemCount              = stemCount;
        _sourcePositions        = new long[_stemCount];
        _fractionalInput        = new double[_stemCount];

        _pendingInput = [.. Enumerable.Range(0, _stemCount).Select(_ => new Queue<SourceChunkInfo>())];
    }

    public async Task Configure(PlaybackSpeedSettings settings, CancellationToken globalToken)
    {
        Trace(settings);

        var speedChanged = Math.Abs(_speed - settings.Speed) > 0.01f;

        _speed = settings.Speed;

        if (globalToken != CancellationToken.None)
            _token = globalToken;

        if (_cts != null && speedChanged)
        {
            await DisposeProcesses().ConfigureAwait(false);
        }
    }


    public Task IsReadyToAcceptStems(CancellationToken token)
    {
        EnsureStemProcesses(_stemCount);
        // Wait until all rings have room (simple check: any one is fine for now)
        return Task.CompletedTask;
    }

    public async Task SubmitStems(IReadOnlyList<AudioBlock> stemBlocks, CancellationToken token)
    {
        Interlocked.Increment(ref _activeIo);
        try
        {
            if (stemBlocks.Count != _stemCount)
            throw new ArgumentException($"Expected {_stemCount} stems, but got {stemBlocks.Count}.");

            EnsureStemProcesses(stemBlocks.Count);

            // No-stretch path: just enqueue into per-stem rings
            if (Math.Abs(_speed - 1.0f) < 0.01f)
            {
                for (int i = 0; i < stemBlocks.Count; i++)
                {
                    if (token.IsCancellationRequested)
                        return;

                    var block = stemBlocks[i];

                    // Track source position for passthrough mode
                    _pendingInput[i].Enqueue(new SourceChunkInfo
                    {
                        SourcePosition = block.Position,
                        SourceFrames = block.Frames
                    });

                    var bytes = MemoryMarshal.AsBytes(block.Buffer.Span);
                    _stemProcesses[i].Ring.Write(bytes, bytes.Length, token);
                }

                return;
            }

            // Stretch path: one ffmpeg+rubberband per stem
            StartProcessesIfNeeded(stemBlocks.Count);

            for (int i = 0; i < stemBlocks.Count; i++)
            {
                var block = stemBlocks[i];

                // Track source position for stretched mode
                _pendingInput[i].Enqueue(new SourceChunkInfo
                {
                    SourcePosition = block.Position,
                    SourceFrames = block.Frames
                });

                var proc  = _stemProcesses[i];

                try
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Only write if ffmpeg is alive
                    if (proc.Stdin != null && !(proc.Ff?.Proc?.HasExited ?? true))
                    {
                        await block.Buffer.WriteAsync(proc.Stdin, token).ConfigureAwait(false);

                        try
                        {
                            await proc.Stdin.FlushAsync(token).ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException)
                        {
                            // ffmpeg exited early
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore write errors (ffmpeg may exit early)
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeIo);
        }
    }


    public async Task<TimeStretchedAudioBlock[]> ReceiveStems(CancellationToken token)
    {
        Interlocked.Increment(ref _activeIo);
        try
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
                    return [];

                // Determine block size (final block may be smaller)
                int bytesToRead    = Math.Min(bytesPerBlock, available);
                int samplesToRead  = bytesToRead / sizeof(float);
                int framesToRead   = samplesToRead / 2;

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
                    return [];

                // Now allocate the float buffer
                var outBuf = _pool.Rent(samplesToRead);
                outBuf.Length = samplesToRead;

                // Copy temp[] → float buffer (safe, no await)
                var outBytes = MemoryMarshal.AsBytes(outBuf.Span);
                temp.AsSpan().CopyTo(outBytes);

                //
                // *** Correct source-position mapping ***
                //
                long sourcePos = ComputeSourcePosition(i, framesToRead);

                result[i] = new TimeStretchedAudioBlock(
                    outBuf,
                    framesToRead,
                    2,
                    _sampleRate,
                    sourcePos);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _activeIo);
        }
    }

private long ComputeSourcePosition(int stemIndex, int outputFrames)
    {
        // inputFrames = outputFrames / speed
        double neededInputFrames = outputFrames / _speed;

        // Add fractional requirement
        _fractionalInput[stemIndex] += neededInputFrames;

        long sourcePos = 0;
        bool first = true;

        var queue = _pendingInput[stemIndex];

        // If no input chunks exist (warm-up), return last known position
        if (queue.Count == 0)
            return _sourcePositions[stemIndex];

        while (_fractionalInput[stemIndex] >= 1 && queue.Count > 0)
        {
            var chunk = queue.Peek();

            int take = (int)Math.Min(chunk.SourceFrames, Math.Floor(_fractionalInput[stemIndex]));

            if (take <= 0)
                break;

            if (first)
            {
                sourcePos = chunk.SourcePosition;
                first = false;
            }

            chunk.SourceFrames -= take;
            _fractionalInput[stemIndex] -= take;

            if (chunk.SourceFrames == 0)
                queue.Dequeue();
        }

        // If we consumed nothing (fraction < 1), use last known position
        if (first)
            sourcePos = _sourcePositions[stemIndex];

        _sourcePositions[stemIndex] = sourcePos;
        return sourcePos;
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

        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var proc in _stemProcesses)
                {
                    if (proc.Stdout == null)
                        continue;

                    var read = await proc.Stdout.ReadAsync(buf, token).ConfigureAwait(false);
                    if (read > 0)
                        proc.Ring.Write(buf, read, token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose; let outer while exit via token
                return;
            }
            catch { }
        }
    }

    private async Task DisposeProcesses()
    {
        Trace();

        if (_cts != null)
        {
            Msg("Cancelling RubberBand/ffmpeg reader task...");
            try { await _cts.CancelAsync(); } catch { }
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

        _cts?.Dispose();
        _cts = null;

        // Reset position tracking
        for (int i = 0; i < _stemCount; i++)
        {
            _pendingInput[i].Clear();
            _fractionalInput[i] = 0;
            _sourcePositions[i] = 0;
        }

        _readerTask = null;

        _cts = null;

        // Wait until no active I/O before tearing down
        while (Interlocked.CompareExchange(ref _activeIo, 0, 0) != 0)
            await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);

    }

    public async ValueTask DisposeAsync()
    {
        await DisposeProcesses().ConfigureAwait(false);
    }
}
