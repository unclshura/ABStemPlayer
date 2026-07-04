using System.Diagnostics;

namespace AudioCore.Impl;

public sealed class FfmpegPipe : IDisposable
{
    private readonly string _path;
    private Process? _proc;
    private Stream? _stdout;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalSamples { get; }

    public FfmpegPipe(string path, int sampleRate = 44100, int channels = 2)
    {
        _path = path;
        SampleRate = sampleRate;
        Channels = channels;

        // Optional: probe duration
        TotalSamples = ProbeTotalSamples(path, sampleRate);

        StartProcess(0);
    }

    private void StartProcess(long startSample)
    {
        var startSeconds = (double)startSample / SampleRate;

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
            $"-hide_banner -loglevel error " +
            $"-nostdin " +                     // prevent console attach
            $"-ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"-i \"{_path}\" " +
            $"-f f32le -ac {Channels} -ar {SampleRate} pipe:1",

            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,         // prevents console window
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden,
            ErrorDialog            = false
        };

        _proc = Process.Start(psi);
        _stdout = _proc!.StandardOutput.BaseStream;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var bytesNeeded = count * sizeof(float);
        var tmp = new byte[bytesNeeded];

        var readBytes = _stdout!.Read(tmp, 0, bytesNeeded);
        if (readBytes <= 0)
            return 0;

        Buffer.BlockCopy(tmp, 0, buffer, offset * sizeof(float), readBytes);

        return readBytes / sizeof(float);
    }

    public void Seek(long sampleIndex)
    {
        DisposeProcessOnly();
        StartProcess(sampleIndex);
    }

    public void Reset() => Seek(0);

    private static long ProbeTotalSamples(string path, int sampleRate)
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
        try { _stdout?.Dispose(); } catch { }
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        try { _proc?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        DisposeProcessOnly();
    }
}
