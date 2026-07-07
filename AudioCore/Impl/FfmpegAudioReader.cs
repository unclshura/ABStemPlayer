using System.Diagnostics;

namespace AudioCore.Impl;

public sealed class FfmpegAudioReader : IAudioReader, IDisposable
{
    private readonly string _path;

    // Lazy process wrapper
    private Lazy<FfmpegProcess> _process;

    // Remember last seek position
    private long _pendingSeekSample = 0;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalSamples { get; }
    public TimeSpan Duration { get; }

    public FfmpegAudioReader(string path)
    {
        _path = path;

        var probe = FfprobeProcess.ProbeAudio(path);

        SampleRate = probe.SampleRate;
        Channels = probe.Channels;
        TotalSamples = probe.TotalSamples;
        Duration = probe.Duration;

        _process = CreateLazyProcess();
    }

    private Lazy<FfmpegProcess> CreateLazyProcess() => new Lazy<FfmpegProcess>(() =>
        {
            var startSeconds = (double)_pendingSeekSample / SampleRate;

            var cmd =
                "-hide_banner -loglevel error " +
                "-nostdin " +
                $"-ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-i \"{_path}\" " +
                $"-f f32le -ac {Channels} -ar {SampleRate} pipe:1";

            var p = new FfmpegProcess(
                name: $"pipe:{_path}",
                commandLine: cmd,
                redirectOutput: true,
                redirectInput: true);

            p.StartProcess();
            return p;
        });

    public int Read(float[] buffer, int offset, int count)
    {
        var proc = _process.Value; // starts process if not started
        if (proc.Stdout is null)
            return 0;

        return proc.Read(buffer, offset, count);
    }

    public void Seek(long sampleIndex)
    {
        _pendingSeekSample = sampleIndex;
        DisposeProcessOnly();
        _process = CreateLazyProcess(); // new lazy instance
    }

    public void Reset()
    {
        Seek(0);
    }

    private void DisposeProcessOnly()
    {
        if (_process.IsValueCreated)
        {
            try { _process.Value.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        DisposeProcessOnly();
    }
}
