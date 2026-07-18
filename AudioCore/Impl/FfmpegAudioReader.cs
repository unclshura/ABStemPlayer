using System.Diagnostics;
using System.Globalization;

namespace AudioCore.Impl;

public sealed class FfmpegAudioReader : IAudioReader, IDisposable
{
    private readonly string _path;

    private Lazy<FfmpegProcess> _process;

    private long _pendingSeekSample = 0;

    // NEW: internal position tracking (in floats)
    private long _pos = 0;

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

    private Lazy<FfmpegProcess> CreateLazyProcess() =>
        new Lazy<FfmpegProcess>(() =>
        {
            var startSeconds = (double)_pendingSeekSample / SampleRate;

            var cmd =
                "-hide_banner -loglevel error " +
                "-nostdin " +
                $"-i \"{_path}\" " +                     // input first
                $"-ss {startSeconds.ToString(CultureInfo.InvariantCulture)} " + // output seek
                $"-f f32le -ac {Channels} -ar {SampleRate} pipe:1";


            var p = new FfmpegProcess(
                name: $"pipe:{_path}",
                commandLine: cmd,
                redirectOutput: true,
                redirectInput: false);

            p.StartProcess();
            return p;
        });

    /// <summary>
    /// Async float reader using new FfmpegProcess.ReadAsync
    /// </summary>
    public async Task<int> ReadAsync(Memory<float> buffer, CancellationToken token)
    {
        var proc = _process.Value;
        if (proc.Stdout is null)
            return 0;

        int readFloats = await proc.ReadAsync(buffer, token).ConfigureAwait(false);

        // NEW: update internal position
        _pos += readFloats;

        return readFloats;
    }

    public void Seek(long sampleIndex)
    {
        _pendingSeekSample = sampleIndex;

        // NEW: update internal position (floats)
        _pos = sampleIndex * Channels;

        DisposeProcessOnly();
        _process = CreateLazyProcess();
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
