using System.Text;
using SnapStack.Core;

namespace SnapStack.Clipboard;

internal static class RtfImageStackFormatter
{
    private const string HexDigits = "0123456789abcdef";

    public static string Format(IReadOnlyList<CapturedImage> captures)
    {
        var builder = new StringBuilder();
        builder.Append("{\\rtf1\\ansi\\deff0\n");

        foreach (var capture in captures.OrderBy(item => item.Sequence))
        {
            builder
                .Append("{\\pict\\pngblip\\picw")
                .Append(capture.PixelWidth)
                .Append("\\pich")
                .Append(capture.PixelHeight)
                .Append(' ');

            foreach (var value in capture.PngBytes.Span)
            {
                builder.Append(HexDigits[value >> 4]);
                builder.Append(HexDigits[value & 0x0F]);
            }

            builder.Append("}\\par\n");
        }

        builder.Append('}');
        return builder.ToString();
    }
}
