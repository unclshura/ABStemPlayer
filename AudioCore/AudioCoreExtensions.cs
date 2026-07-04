using System.Diagnostics.CodeAnalysis;
using AudioCore.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace AudioCore;

public static class AudioCoreExtensions
{
    [ExcludeFromCodeCoverage]
	public static ServiceCollection AddAudioCore(this ServiceCollection services)
	{
        services.AddSingleton<AudioBufferPool>();
        services.AddSingleton<ByteBufferPool>();
        services.AddSingleton<IStemDecoderFactory, StemDecoderFactory>();
        services.AddSingleton<IAudioReaderFactory, FfmpegAudioReaderFactory>();
        services.AddSingleton<IStemWaveformService, StemWaveformService>();

        services.AddTransient<IStemDecoder, StemDecoder>();
        services.AddTransient<IAudioMixer, AudioMixer>();
        services.AddTransient<ITimeStretchEngine, RubberBandTimeStretchEngine>();
        services.AddTransient<IStemSeparator, Htdemucs6sSeparator>();

        services.AddSingleton<IAudioOutputDevice>(sp =>
            new WasapiOutputDevice(sp.GetRequiredService<ByteBufferPool>(), channels: 2));

        services.AddTransient<IStemPlaybackEngine, StemPlaybackEngine>();

        return services;
	}
}
