using SnapStack.Core;

namespace SnapStack.Tests;

[TestClass]
public sealed class CaptureSessionTests
{
    [TestMethod]
    public void StartAddStopPreservesCaptureOrder()
    {
        var session = new CaptureSession();

        session.Start();
        var first = session.AddCapture([0x01, 0x02], 100, 50);
        var second = session.AddCapture([0x03, 0x04], 200, 100);
        session.Stop();

        Assert.AreEqual(CaptureSessionState.Ready, session.State);
        Assert.AreEqual(2, session.Count);
        Assert.AreEqual(1, first.Sequence);
        Assert.AreEqual(2, second.Sequence);
        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            session.Captures.Select(capture => capture.Id).ToArray());
    }

    [TestMethod]
    public void StartBeginsAFreshSession()
    {
        var session = new CaptureSession();

        session.Start();
        session.AddCapture([0x01], 10, 10);
        session.Stop();

        session.Start();

        Assert.AreEqual(CaptureSessionState.Capturing, session.State);
        Assert.AreEqual(0, session.Count);
        Assert.IsNull(session.EndedAt);
        Assert.IsNotNull(session.StartedAt);
    }

    [TestMethod]
    public void AddCaptureRequiresAnActiveSession()
    {
        var session = new CaptureSession();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => session.AddCapture([0x01], 10, 10));
    }

    [TestMethod]
    public void CapturedBytesAreCopiedIntoSessionStorage()
    {
        var source = new byte[] { 0x10, 0x20 };
        var session = new CaptureSession();

        session.Start();
        var capture = session.AddCapture(source, 10, 10);
        source[0] = 0xFF;

        CollectionAssert.AreEqual(
            new byte[] { 0x10, 0x20 },
            capture.PngBytes.ToArray());
    }

    [TestMethod]
    public async Task DeferredPngDoesNotBlockSessionInsertion()
    {
        var encoded = new TaskCompletionSource<ReadOnlyMemory<byte>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new CaptureSession();
        session.Start();

        var capture = session.AddDeferredCapture(encoded.Task, 800, 500);

        Assert.AreEqual(1, session.Count);
        Assert.AreEqual(1, capture.Sequence);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = capture.PngBytes);

        encoded.SetResult(new byte[] { 0x89, 0x50 });

        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50 },
            (await capture.GetPngBytesAsync()).ToArray());
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50 },
            capture.PngBytes.ToArray());
    }
}
