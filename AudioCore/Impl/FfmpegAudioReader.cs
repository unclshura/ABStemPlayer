namespace AudioCore.Impl;

public sealed class FfmpegAudioReader : IAudioReader
{
    private readonly FfmpegPipe _pipe;

    public int SampleRate => _pipe.SampleRate;
    public int Channels => _pipe.Channels;
    public long TotalSamples => _pipe.TotalSamples;

    public FfmpegAudioReader(string path)
    {
        _pipe = new FfmpegPipe(path);
    }

    public int Read(float[] buffer, int offset, int count)
        => _pipe.Read(buffer, offset, count);

    public void Seek(long sampleIndex)
        => _pipe.Seek(sampleIndex);

    public void Reset()
        => Seek(0);

    public void Dispose()
        => _pipe.Dispose();
}
