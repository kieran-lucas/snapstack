using SnapStack.Clipboard;
using SnapStack.Core;

namespace SnapStack.Tests;

[TestClass]
public sealed class RtfImageStackFormatterTests
{
    [TestMethod]
    public void FormatOrdersImagesBySequence()
    {
        var second = new CapturedImage(
            Guid.NewGuid(),
            2,
            DateTimeOffset.UtcNow,
            20,
            20,
            new byte[] { 0x20 });

        var first = new CapturedImage(
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            10,
            10,
            new byte[] { 0x10 });

        var rtf = RtfImageStackFormatter.Format([second, first]);

        var firstPosition = rtf.IndexOf(@"\picw10", StringComparison.Ordinal);
        var secondPosition = rtf.IndexOf(@"\picw20", StringComparison.Ordinal);

        Assert.IsTrue(firstPosition >= 0);
        Assert.IsTrue(secondPosition > firstPosition);
    }

    [TestMethod]
    public void FormatEmbedsPngBytesAsHex()
    {
        var capture = new CapturedImage(
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            1,
            1,
            new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var rtf = RtfImageStackFormatter.Format([capture]);

        StringAssert.Contains(rtf, @"\pngblip");
        StringAssert.Contains(rtf, "89504e47");
    }
}
