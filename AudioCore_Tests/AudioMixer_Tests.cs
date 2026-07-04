using AudioCore.Impl;
using AudioCore.Interfaces;
using AudioCore.Models;

namespace AudioCore_Tests;

[TestClass]
public sealed class AudioMixer_Tests
{
    private AudioBufferPool _pool = null!;
    private IAudioMixer _mixer = null!;

    [TestInitialize]
    public void Init()
    {
        _pool = new AudioBufferPool();
        _mixer = new AudioMixer(_pool);
    }

    private AudioBlock MakeBlock(float left, float right, int frames = 4, int sampleRate = 44100)
    {
        var buf = _pool.Rent(frames * 2);
        buf.Length = frames * 2;

        for (var i = 0; i < frames; i++)
        {
            buf.Samples[i * 2 + 0] = left;
            buf.Samples[i * 2 + 1] = right;
        }

        return new AudioBlock(buf, sampleRate, 2, 0);
    }

    [TestMethod]
    public void Mixer_Mixes_Two_Stems()
    {
        var stem1 = MakeBlock(1f, 1f);
        var stem2 = MakeBlock(0.5f, 0.5f);

        var settings = new MixerSettings
        {
            Stems = new[]
            {
                new StemMixSettings { Enabled = true, GainDb = 0, Pan = 0 },
                new StemMixSettings { Enabled = true, GainDb = 0, Pan = 0 }
            }
        };

        var mixed = _mixer.Mix(new[] { stem1, stem2 }, settings);

        var span = mixed.Buffer.Span;

        for (var i = 0; i < mixed.Frames; i++)
        {
            Assert.AreEqual(1.5f, span[i * 2 + 0], 1e-6f);
            Assert.AreEqual(1.5f, span[i * 2 + 1], 1e-6f);
        }

        mixed.Dispose();
        stem1.Dispose();
        stem2.Dispose();
    }

    [TestMethod]
    public void Mixer_Respects_Gain()
    {
        var stem = MakeBlock(1f, 1f);

        var settings = new MixerSettings
        {
            Stems = new[]
            {
                new StemMixSettings { Enabled = true, GainDb = -6, Pan = 0 }
            }
        };

        var mixed = _mixer.Mix(new[] { stem }, settings);

        var expected = MathF.Pow(10f, -6f / 20f); // -6 dB

        var span = mixed.Buffer.Span;

        for (var i = 0; i < mixed.Frames; i++)
        {
            Assert.AreEqual(expected, span[i * 2 + 0], 1e-6f);
            Assert.AreEqual(expected, span[i * 2 + 1], 1e-6f);
        }

        mixed.Dispose();
        stem.Dispose();
    }

    [TestMethod]
    public void Mixer_Respects_Pan()
    {
        var stem = MakeBlock(1f, 1f);

        var settings = new MixerSettings
        {
            Stems = new[]
            {
                new StemMixSettings { Enabled = true, GainDb = 0, Pan = 1f } // full right
            }
        };

        var mixed = _mixer.Mix(new[] { stem }, settings);
        var span = mixed.Buffer.Span;

        for (var i = 0; i < mixed.Frames; i++)
        {
            Assert.AreEqual(0f, span[i * 2 + 0], 1e-6f); // left muted
            Assert.AreEqual(1f, span[i * 2 + 1], 1e-6f); // right full
        }

        mixed.Dispose();
        stem.Dispose();
    }

    [TestMethod]
    public void Mixer_Disabled_Stem_Is_Ignored()
    {
        var stem1 = MakeBlock(1f, 1f);
        var stem2 = MakeBlock(1f, 1f);

        var settings = new MixerSettings
        {
            Stems = new[]
            {
                new StemMixSettings { Enabled = true },
                new StemMixSettings { Enabled = false }
            }
        };

        var mixed = _mixer.Mix(new[] { stem1, stem2 }, settings);
        var span = mixed.Buffer.Span;

        for (var i = 0; i < mixed.Frames; i++)
        {
            Assert.AreEqual(1f, span[i * 2 + 0], 1e-6f);
            Assert.AreEqual(1f, span[i * 2 + 1], 1e-6f);
        }

        mixed.Dispose();
        stem1.Dispose();
        stem2.Dispose();
    }

    [TestMethod]
    public void Mixer_Handles_Mono_Stem()
    {
        // mono block
        var frames = 4;
        var buf = _pool.Rent(frames);
        buf.Length = frames;

        for (var i = 0; i < frames; i++)
            buf.Samples[i] = 2f;

        var monoBlock = new AudioBlock(buf, 44100, 1, 0);

        var settings = new MixerSettings
        {
            Stems = new[]
            {
                new StemMixSettings { Enabled = true }
            }
        };

        var mixed = _mixer.Mix(new[] { monoBlock }, settings);
        var span = mixed.Buffer.Span;

        for (var i = 0; i < mixed.Frames; i++)
        {
            Assert.AreEqual(2f, span[i * 2 + 0], 1e-6f);
            Assert.AreEqual(2f, span[i * 2 + 1], 1e-6f);
        }

        mixed.Dispose();
        monoBlock.Dispose();
    }
}
