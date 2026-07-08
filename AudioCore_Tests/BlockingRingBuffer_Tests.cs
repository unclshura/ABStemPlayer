using AudioCore.Impl;

namespace AudioCore_Tests;

[TestClass]
public sealed class BlockingRingBuffer_Tests
{
    [TestMethod]
    public void WriteAndDrain_Simple()
    {
        var ring = new BlockingRingBuffer(1024);
        var ct   = CancellationToken.None;

        byte[] src = new byte[100];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)i;

        ring.WriteToOutput(src, src.Length, ct);

        Span<byte> dest = stackalloc byte[100];
        int read = ring.DrainRing(dest, dest.Length);

        Assert.AreEqual(100, read);
        for (int i = 0; i < 100; i++)
            Assert.AreEqual((byte)i, dest[i]);
    }

    [TestMethod]
    public void Write_WrapAround()
    {
        var ring = new BlockingRingBuffer(32);
        var ct   = CancellationToken.None;

        // Fill almost full
        byte[] first = new byte[20];
        for (int i = 0; i < first.Length; i++)
            first[i] = (byte)(i + 1);

        ring.WriteToOutput(first, first.Length, ct);

        // Drain a bit to force wrap
        Span<byte> tmp = stackalloc byte[10];
        int drained = ring.DrainRing(tmp, tmp.Length);
        Assert.AreEqual(10, drained);

        // Now write again, forcing wrap-around
        byte[] second = new byte[15];
        for (int i = 0; i < second.Length; i++)
            second[i] = (byte)(100 + i);

        ring.WriteToOutput(second, second.Length, ct);

        // Drain everything
        Span<byte> dest = stackalloc byte[25];
        int read = ring.DrainRing(dest, dest.Length);

        Assert.AreEqual(25, read);

        // First 10 were drained earlier, so remaining 10 from first + 15 from second
        for (int i = 0; i < 10; i++)
            Assert.AreEqual((byte)(i + 11), dest[i]);

        for (int i = 0; i < 15; i++)
            Assert.AreEqual((byte)(100 + i), dest[10 + i]);
    }

    [TestMethod]
    public void Write_Blocks_WhenFull_And_Unblocks_WhenDrained()
    {
        var ring = new BlockingRingBuffer(64);
        var cts  = new CancellationTokenSource();

        byte[] src = new byte[63]; // fills ring completely (63 bytes free)
        ring.WriteToOutput(src, src.Length, CancellationToken.None);

        bool writeCompleted = false;

        var writerThread = new Thread(() =>
        {
            try
            {
                // This should block until space is freed
                ring.WriteToOutput(new byte[10], 10, cts.Token);
                writeCompleted = true;
            }
            catch (OperationCanceledException)
            {
            }
        });

        writerThread.Start();

        // Let writer block
        Thread.Sleep(50);
        Assert.IsFalse(writeCompleted, "Writer should be blocked");

        // Drain some space
        Span<byte> drain = stackalloc byte[20];
        int drained = ring.DrainRing(drain, drain.Length);
        Assert.IsGreaterThan(0, drained);

        // Writer should now complete
        Thread.Sleep(50);
        Assert.IsTrue(writeCompleted, "Writer should unblock after draining");
    }

    [TestMethod]
    public void Write_Cancels_WhenFull()
    {
        var ring = new BlockingRingBuffer(32);
        var cts  = new CancellationTokenSource();

        // Fill ring
        ring.WriteToOutput(new byte[31], 31, CancellationToken.None);

        bool canceled = false;

        var writerThread = new Thread(() =>
        {
            try
            {
                ring.WriteToOutput(new byte[10], 10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
        });

        writerThread.Start();

        Thread.Sleep(50);
        cts.Cancel();

        writerThread.Join();

        Assert.IsTrue(canceled, "Writer should throw OperationCanceledException");
    }

    [TestMethod]
    public void WaitForOutput_ReturnsAvailable()
    {
        var ring = new BlockingRingBuffer(128);
        var ct   = CancellationToken.None;

        byte[] src = new byte[50];
        ring.WriteToOutput(src, src.Length, ct);

        int available = ring.WaitForOutput(ct);

        Assert.AreEqual(50, available);
    }

    [TestMethod]
    public void ResetRing_ClearsBuffer()
    {
        var ring = new BlockingRingBuffer(128);
        var ct   = CancellationToken.None;

        ring.WriteToOutput(new byte[60], 60, ct);

        ring.ResetRing();

        Span<byte> dest = stackalloc byte[128];
        int read = ring.DrainRing(dest, dest.Length);

        Assert.AreEqual(0, read);
    }
}
