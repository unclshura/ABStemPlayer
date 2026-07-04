using NAudio.Wave;

namespace AudioCore_Tests;

public sealed class FakeWasapiOut : IWavePlayer
{
    public bool Played { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public IWaveProvider? Provider { get; private set; }

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    public float Volume { get; set; } = 1.0f;

    public WaveFormat? OutputWaveFormat => Provider?.WaveFormat;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider)
    {
        Provider = waveProvider;
    }

    public void Play()
    {
        Played = true;
        PlaybackState = PlaybackState.Playing;
    }

    public void Stop()
    {
        Stopped = true;
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Pause()
    {
        PlaybackState = PlaybackState.Paused;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
