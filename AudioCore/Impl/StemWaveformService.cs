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
        {
            return Array.Empty<float>();
        }

        var value = await TryReadingFromCache(stem, segments);
        if (value != null)
            return value;

        var totalFrames = (long)(decoder.Stem.Duration.TotalSeconds * decoder.Stem.SampleRate);
        var framesPerSegment = Math.Max(1,totalFrames / segments);

        var result = new float[segments];

        decoder.Reset();

        for (var i = 0; i < segments; i++)
        {
            var segmentStart = framesPerSegment * i;
            decoder.Seek(segmentStart);

            var sum = 0f;
            var count = 0;

            // Decode only one block per segment
            if (decoder.TryDecodeNextBlock(out var block))
            {
                try
                {
                    var span = block.Span;
                    var channels = decoder.Stem.Channels;

                    for (var s = 0; s < span.Length; s++)
                    {
                        var v = span[s];
                        sum += Math.Abs(v);
                    }

                    count = span.Length;
                }
                finally
                {
                    block.Dispose();
                }
            }

            result[i] = count > 0 ? sum / count : 0f;

            await Task.Yield();
        }

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
