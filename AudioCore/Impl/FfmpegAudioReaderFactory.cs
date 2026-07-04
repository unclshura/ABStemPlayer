namespace AudioCore.Impl;

public sealed class FfmpegAudioReaderFactory : IAudioReaderFactory
{
    public IAudioReader Create(string filePath)
    {
        return new FfmpegAudioReader(filePath);
    }
}
