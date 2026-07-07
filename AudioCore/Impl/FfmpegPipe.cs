using System.Diagnostics;

namespace AudioCore.Impl;

public sealed class FfmpegPipe : IDisposable
{
    private readonly string _path;
    private FfmpegProcess? _process;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalSamples { get; }

    public FfmpegPipe(string path, int sampleRate = 44100, int channels = 2)
    {
        _path = path;
        SampleRate = sampleRate;
        Channels = channels;

        TotalSamples = ProbeTotalSamples(path, sampleRate);

        StartProcess(0);
    }

    private void StartProcess(long startSample)
    {
        var startSeconds = (double)startSample / SampleRate;

        var cmd =
            "-hide_banner -loglevel error " +
            "-nostdin " +
            $"-ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"-i \"{_path}\" " +
            $"-f f32le -ac {Channels} -ar {SampleRate} pipe:1";

        _process = new FfmpegProcess(
            name: $"pipe:{_path}",
            commandLine: cmd,
            redirectOutput: true,
            redirectInput: true);

        _process.StartProcess();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_process?.Stdout is null)
            return 0;

        return _process.Read(buffer, offset, count);
    }

    public void Seek(long sampleIndex)
    {
        DisposeProcessOnly();
        StartProcess(sampleIndex);
    }

    public void Reset()
    {
        Seek(0);
    }

    private static long ProbeTotalSamples(string path, int sampleRate) => FfmpegProcess.ProbeTotalSamples(path, sampleRate);
    private void DisposeProcessOnly()
    {
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        DisposeProcessOnly();
    }
}
