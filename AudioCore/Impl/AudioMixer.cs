namespace AudioCore.Impl;

public sealed class AudioMixer : IAudioMixer
{
    private readonly AudioBufferPool _pool;

    private const int _outputChannels = 2;

    public AudioMixer(AudioBufferPool pool)
    {
        _pool = pool;
    }

    public MixedAudioBlock Mix(
        IReadOnlyList<TimeStretchedAudioBlock> stemBlocks,
        MixerSettings settings)
    {
        if (stemBlocks.Count == 0)
            throw new ArgumentException("No stems provided");

        // All blocks must have same sample rate and frame count
        var first      = stemBlocks[0];
        var frames     = first.Frames;
        var sampleRate = first.SampleRate;
        var position   = first.Position;

        // Rent output buffer
        var outBuf = _pool.Rent(frames * _outputChannels);
        outBuf.Length = frames * _outputChannels;

        Span<float> outSpan = outBuf.Span;
        outSpan.Clear();

        for (var stem = 0; stem < stemBlocks.Count; stem++)
        {
            var s = settings.Stems[stem];
            if (!s.Enabled)
                continue;

            var block = stemBlocks[stem];
            Span<float> inSpan = block.Buffer.Span;

            var inChannels = block.Channels;

            var gain = DbToLinear(s.GainDb);
            var pan = s.Pan;

            var leftGain = gain * (pan <= 0 ? 1f : 1f - pan);
            var rightGain = gain * (pan >= 0 ? 1f : 1f + pan);

            for (var i = 0; i < frames; i++)
            {
                if (i * inChannels + 1 >= inSpan.Length)
                {
                    outSpan[i * 2 + 0] = 0f;
                    outSpan[i * 2 + 1] = 0f;
                    continue;
                }

                var l = inChannels > 1 ? inSpan[i * inChannels + 0] : inSpan[i];
                var r = inChannels > 1 ? inSpan[i * inChannels + 1] : inSpan[i];

                outSpan[i * 2 + 0] += l * leftGain;
                outSpan[i * 2 + 1] += r * rightGain;
            }
        }


        return new MixedAudioBlock(outBuf, frames, _outputChannels, sampleRate, position);
    }

    private static float DbToLinear(float db)
        => db <= -80f ? 0f : MathF.Pow(10f, db / 20f);
}