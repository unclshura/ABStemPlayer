namespace AudioCore.Impl;

public sealed class StemWaveformService : IStemWaveformService
{
    private readonly AudioBufferPool _bufferPool;

    public StemWaveformService(AudioBufferPool bufferPool)
    {
        _bufferPool = bufferPool;
    }

    public async Task<float[]> ComputeWaveformAsync(StemTrack stem, IStemDecoder decoder, int segments = 200)
    {
        if (segments <= 0)
            return Array.Empty<float>();

        var cached = await TryReadingFromCache(stem, segments);
        if (cached != null)
            return cached;

        var totalFrames = (long)(stem.Duration.TotalSeconds * stem.SampleRate);
        var framesPerSegment = Math.Max(1, totalFrames / segments);

        var sums   = new float[segments];
        var counts = new int[segments];

        decoder.Reset();

        long globalFramePos = 0;

        while (true)
        {
            var block = await decoder.DecodeNextBlockAsync(CancellationToken.None);
            if (block == null)
                break;

            try
            {
                var span     = block.Value.Span;
                var channels = stem.Channels;
                var frames   = block.Value.Frames;

                for (int f = 0; f < frames; f++)
                {
                    long frameIndex = globalFramePos + f;
                    int segmentIndex = (int)(frameIndex / framesPerSegment);

                    if (segmentIndex >= segments)
                        break;

                    // accumulate absolute amplitude across channels
                    float sum = 0f;
                    int baseIndex = f * channels;

                    for (int c = 0; c < channels; c++)
                        sum += Math.Abs(span[baseIndex + c]);

                    sums[segmentIndex] += sum;
                    counts[segmentIndex] += channels;
                }
            }
            finally
            {
                globalFramePos += block.Value.Frames;
                block.Value.Dispose();
            }
        }

        var result = new float[segments];
        for (int i = 0; i < segments; i++)
            result[i] = counts[i] > 0 ? sums[i] / counts[i] : 0f;

        await SaveToCache(stem, result);
        return result;
    }

    private async Task SaveToCache(StemTrack stem, float[] result)
    {
        var cahcheFile = GetCahcheFileName(stem);
        if (cahcheFile == null)
            return;

        var bytes = new byte[result.Length * sizeof(float)];
        Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
        await File.WriteAllBytesAsync(cahcheFile, bytes);
    }

    private static async Task<float[]?> TryReadingFromCache(StemTrack stem, int segments)
    {
        var cahcheFile = GetCahcheFileName(stem);
        if (!File.Exists(cahcheFile))
            return null;

        var cachedData = await File.ReadAllBytesAsync(cahcheFile);
        if (cachedData.Length == segments * sizeof(float))
        {
            var res = new float[segments];
            Buffer.BlockCopy(cachedData, 0, res, 0, cachedData.Length);
            return res;
        }

        return null;
    }

    private static string? GetCahcheFileName(StemTrack stem) =>
        string.IsNullOrWhiteSpace(stem.FilePath) ? null : Path.Combine(Path.GetDirectoryName(stem.FilePath)!, $"{Path.GetFileNameWithoutExtension(stem.FilePath)}.waveform");
}
