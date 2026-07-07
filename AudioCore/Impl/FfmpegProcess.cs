using System.Diagnostics;

namespace AudioCore.Impl;

public sealed class FfmpegProcess : IDisposable
{
    public Process? Proc  { get; private set; }
    public Stream? Stdout { get; private set; }
    public Stream? Stdin  { get; private set; }

    private string _name;
    private string _commandLine;
    private bool   _redirectOutput;
    private bool   _redirectInput;

    public FfmpegProcess(string name, string commandLine, bool redirectOutput = true, bool redirectInput = true)
    {
        _name           = name;
        _commandLine    = commandLine;
        _redirectOutput = redirectOutput;
        _redirectInput  = redirectInput;
    }

    public void StartProcess()
    {
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

        if ( _redirectInput)
            Stdin = Proc.StandardInput.BaseStream;
        if (_redirectOutput)
            Stdout = Proc.StandardOutput.BaseStream;

        // Start draining stderr immediately
        _ = Task.Run(() => DrainStderr(Proc));
    }

    private void DrainStderr(Process proc)
    {
        try
        {
            var reader = proc.StandardError;

            // ffmpeg writes short lines, so ReadLine is fine
            // If you want zero allocations, use ReadAsync into a rented buffer.
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                Debug.WriteLine($"{_name}: {line}");
            }
        }
        catch
        {
            // ignore exceptions during stderr drain, as the process may have exited
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var bytesNeeded = count * sizeof(float);
        var tmp = new byte[bytesNeeded];

        var readBytes = Stdout!.Read(tmp, 0, bytesNeeded);
        if (readBytes <= 0)
            return 0;

        Buffer.BlockCopy(tmp, 0, buffer, offset * sizeof(float), readBytes);

        return readBytes / sizeof(float);
    }

    public static long ProbeTotalSamples(string path, int sampleRate)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffprobe",
            Arguments              = $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"",
            RedirectStandardOutput = true,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden,
            UseShellExecute        = false
        };

        using var p = Process.Start(psi);
        var s = p!.StandardOutput.ReadToEnd();
        p.WaitForExit();

        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return (long)(seconds * sampleRate);
        }

        return 0;
    }

    private void DisposeProcessOnly()
    {
        try { Stdout?.Dispose(); } catch { }
        try { Stdin?.Dispose(); } catch { }
        try { Proc?.StandardError.BaseStream?.Dispose(); } catch { }
        try { if (Proc != null && !Proc.HasExited) Proc.Kill(); } catch { }
        try { Proc?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        DisposeProcessOnly();
    }
}
