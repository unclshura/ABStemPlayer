using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace AudioCore.Impl;

public sealed class WasapiOutputDevice : IAudioOutputDevice, IDisposable
{
    private readonly IWavePlayer          _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveFormat           _mixFormat = null!;
    private readonly ByteBufferPool       _pool;
    private readonly bool                 _isFloat;

    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>
    /// For tests only, exposes the underlying buffer to allow direct writes.
    /// </summary>
    public BufferedWaveProvider Buffer => _buffer;

    /// <summary>
    /// For tests only
    /// </summary>
    public WasapiOutputDevice(ByteBufferPool pool, IWavePlayer output, int sampleRate = 44100, int channels = 2)
    {
        _pool      = pool;
        _mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _isFloat   = true;
        SampleRate = sampleRate;
        Channels   = channels;

        _buffer = new BufferedWaveProvider(_mixFormat)
        {
            DiscardOnBufferOverflow = true
        };

        _out = output;
        _out.Init(_buffer);
    }

    [ExcludeFromCodeCoverage]
    public WasapiOutputDevice(ByteBufferPool pool, int channels = 2)
    {
        Channels = channels;
        _pool = pool;

        // Get default audio device
        var device = new MMDeviceEnumerator()
            .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Use the system mix format (required for shared mode)
        _mixFormat = device.AudioClient.MixFormat;
        SampleRate = _mixFormat.SampleRate;
        _isFloat   = _mixFormat.Encoding == WaveFormatEncoding.Extensible &&
               ((WaveFormatExtensible)_mixFormat).SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

        // Create buffer in mix format
        _buffer = new BufferedWaveProvider(_mixFormat)
        {
            BufferLength = _mixFormat.AverageBytesPerSecond / 2, // 0.5 seconds
            DiscardOnBufferOverflow = false
        };

        // Event-driven WASAPI mode (true = event mode)
        _out = new WasapiOut(device, AudioClientShareMode.Shared, false, 10);
        _out.Init(_buffer);
    }

    public void Start() => _out.Play();
    public void Stop() => _out.Stop();

    public void Write(ReadOnlySpan<float> samples)
    {
        // Convert float -> PCM16 or float passthrough depending on mix format
        byte[] bytes;

        if (_isFloat)
        {
            // Device supports float32 directly
            bytes = MemoryMarshal.AsBytes(samples).ToArray();
            Send(bytes);
        }
        else
        {
            // Convert float -> PCM16
            int count = samples.Length;
            using var buffer = _pool.Rent(count * 2);

            int bi = 0;
            for (int i = 0; i < count; i++)
            {
                short pcm = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                buffer.Samples[bi++] = (byte)(pcm & 0xFF);
                buffer.Samples[bi++] = (byte)((pcm >> 8) & 0xFF);
            }
            Send(buffer.Samples);
        }

    }

    private void Send(byte[] bytes)
    {
        // Wait until buffer has enough free space
        while (_buffer.BufferedBytes + bytes.Length > _buffer.BufferLength)
        {
            // Sleep a tiny amount to let WASAPI consume data
            Thread.Sleep(2);
        }

        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _out.Dispose();
    }
}