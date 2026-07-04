using System.Runtime.InteropServices;
using NAudio.Wave;
using AudioCore.Impl;

namespace AudioCore_Tests;

[TestClass]
public sealed class WasapiOutputDevice_Tests
{
    [TestMethod]
    public void Constructor_Sets_Properties()
    {
        var fake = new FakeWasapiOut();
        var dev = new WasapiOutputDevice(new ByteBufferPool(), fake, 48000, 1);

        Assert.AreEqual(48000, dev.SampleRate);
        Assert.AreEqual(1, dev.Channels);
    }

    [TestMethod]
    public void Start_Calls_Play()
    {
        var fake = new FakeWasapiOut();
        var dev = new WasapiOutputDevice(new ByteBufferPool(), fake);

        dev.Start();

        Assert.IsTrue(fake.Played);
        Assert.AreEqual(PlaybackState.Playing, fake.PlaybackState);
    }

    [TestMethod]
    public void Stop_Calls_Stop()
    {
        var fake = new FakeWasapiOut();
        var dev = new WasapiOutputDevice(new ByteBufferPool(), fake);

        dev.Start();
        dev.Stop();

        Assert.IsTrue(fake.Stopped);
        Assert.AreEqual(PlaybackState.Stopped, fake.PlaybackState);
    }

    [TestMethod]
    public void Write_Adds_Bytes_To_Buffer()
    {
        var fake = new FakeWasapiOut();
        var dev = new WasapiOutputDevice(new ByteBufferPool(), fake);

        float[] samples = { 1f, -1f, 0.5f, -0.5f };
        dev.Write(samples);

        var buffer = dev.Buffer;

        var outBytes = new byte[buffer.BufferedBytes];
        buffer.Read(outBytes, 0, outBytes.Length);

        // Convert back to float32
        var decoded = MemoryMarshal.Cast<byte, float>(outBytes).ToArray();

        CollectionAssert.AreEqual(samples, decoded);
    }

    [TestMethod]
    public void Dispose_Disposes_Output()
    {
        var fake = new FakeWasapiOut();
        var dev = new WasapiOutputDevice(new ByteBufferPool(), fake);

        dev.Dispose();

        Assert.IsTrue(fake.Disposed);
    }
}
