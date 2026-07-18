using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioCore.Impl;

public sealed class FfmpegProcess : IDisposable
{
    public Process? Proc { get; private set; }
    public Stream? Stdout { get; private set; }
    public Stream? Stdin { get; private set; }

    private readonly string _name;
    private readonly string _commandLine;
    private readonly bool   _redirectOutput;
    private readonly bool   _redirectInput;

    private Task? _stderrTask;

    public FfmpegProcess(string name, string commandLine, bool redirectOutput = true, bool redirectInput = true)
    {
        _name = name;
        _commandLine = commandLine;
        _redirectOutput = redirectOutput;
        _redirectInput = redirectInput;
    }

    public void StartProcess()
    {
        Debug.WriteLine($"{_name}: Starting ffmpeg process");

        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            Arguments              = _commandLine,
            UseShellExecute        = false,
            RedirectStandardOutput = _redirectOutput,
            RedirectStandardError  = true,
            RedirectStandardInput  = _redirectInput,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden,
            ErrorDialog            = false
        };

        Proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg process");

        if (_redirectInput)
            Stdin = Proc.StandardInput.BaseStream;
        if (_redirectOutput)
            Stdout = Proc.StandardOutput.BaseStream;

        _stderrTask = Task.Run(() => DrainStderrAsync(Proc));
    }

    private async Task DrainStderrAsync(Process proc)
    {
        try
        {
            using var reader = proc.StandardError;
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;

                Debug.WriteLine($"{_name}: {line}");
            }
        }
        catch
        {
        }
    }

    public async Task<int> ReadAsync(Memory<float> buffer, CancellationToken token)
    {
        if (Stdout is null)
            return 0;

        int maxBytes = buffer.Length * sizeof(float);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(maxBytes);

        try
        {
            int readBytes = await Stdout.ReadAsync(tmp.AsMemory(0, maxBytes), token)
                                        .ConfigureAwait(false);
            if (readBytes <= 0)
                return 0;

            int floatsRead = readBytes / sizeof(float);
            var floatMem   = buffer.Slice(0, floatsRead);

            // Copy raw bytes into the caller's float buffer
            var floatSpan = floatMem.Span;
            var byteSpan  = MemoryMarshal.AsBytes(floatSpan);
            tmp.AsSpan(0, readBytes).CopyTo(byteSpan);

            return floatsRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (Stdin is null)
            throw new InvalidOperationException("StdIn is not redirected");
        await Stdin.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken token)
    {
        if (Stdin is null)
            throw new InvalidOperationException("StdIn is not redirected");
        return Stdin.FlushAsync(token);
    }

    private void DisposeProcessOnly()
    {
        if (Proc != null)
            Debug.WriteLine($"{_name}: Disposing ffmpeg process");

        try { Stdout?.Dispose(); Stdout = null; } catch { }
        try { Stdin?.Dispose(); Stdin = null; } catch { }
        try { Proc?.StandardError.BaseStream?.Dispose(); } catch { }
        try { if (Proc != null && !Proc.HasExited) Proc.Kill(); } catch { }
        try { Proc?.Dispose(); Proc = null; } catch { }
    }

    public void Dispose()
    {
        DisposeProcessOnly();
    }
}
