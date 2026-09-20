using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapStack.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace SnapStack.Clipboard;

public sealed class SequentialPasteService
{
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const ushort VkV = 0x56;

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    public async Task PasteAsync(
        IReadOnlyList<CapturedImage> captures,
        CancellationToken cancellationToken = default)
    {
        if (captures.Count == 0)
        {
            throw new ArgumentException(
                "At least one captured image is required.",
                nameof(captures));
        }

        await WaitForModifiersReleasedAsync(cancellationToken);

        foreach (var capture in captures.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await PublishBitmapAsync(capture);

            // Give the destination application time to observe the new
            // clipboard sequence number before injecting Ctrl+V.
            await Task.Delay(60, cancellationToken);

            SendPasteShortcut();

            // Rich editors and chat clients often process image pastes
            // asynchronously. Avoid replacing the clipboard too quickly.
            await Task.Delay(180, cancellationToken);
        }
    }

    private static async Task PublishBitmapAsync(CapturedImage capture)
    {
        using var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(capture.PngBytes.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);

        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };

        dataPackage.Properties.Title =
            $"SnapStack capture {capture.Sequence}";

        dataPackage.SetBitmap(
            RandomAccessStreamReference.CreateFromStream(stream));

        var options = new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false
        };

        if (!Windows.ApplicationModel.DataTransfer.Clipboard.SetContentWithOptions(
                dataPackage,
                options))
        {
            throw new InvalidOperationException(
                "Windows could not set the fallback bitmap clipboard content.");
        }

        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private static async Task WaitForModifiersReleasedAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (AnyModifierPressed())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Release Ctrl/Shift/Alt/Windows keys before fallback paste.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static bool AnyModifierPressed()
    {
        return IsKeyPressed(VkControl)
            || IsKeyPressed(VkShift)
            || IsKeyPressed(VkMenu)
            || IsKeyPressed(VkLWin)
            || IsKeyPressed(VkRWin);
    }

    private static bool IsKeyPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static void SendPasteShortcut()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl),
            KeyboardInput(VkV),
            KeyboardInput(VkV, keyUp: true),
            KeyboardInput(VkControl, keyUp: true)
        };

        var sent = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<INPUT>());

        if (sent != inputs.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not inject the fallback Ctrl+V shortcut.");
        }
    }

    private static INPUT KeyboardInput(
        ushort virtualKey,
        bool keyUp = false)
    {
        return new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] INPUT[] inputs,
        int inputSize);
}
