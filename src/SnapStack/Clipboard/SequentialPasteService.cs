using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapStack.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace SnapStack.Clipboard;

public sealed class SequentialPasteService
{
    private static readonly TimeSpan ClipboardReadyDelay =
        TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan DestinationPasteDelay =
        TimeSpan.FromMilliseconds(350);

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

        await WaitForShortcutKeysReleasedAsync(cancellationToken);

        foreach (var capture in captures.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await capture.GetPngBytesAsync();

            await PublishBitmapAsync(capture);

            // Give the destination application time to observe the new
            // clipboard sequence number before injecting Ctrl+V.
            await Task.Delay(ClipboardReadyDelay, cancellationToken);

            SendPasteShortcut();

            // Rich editors and chat clients often process image pastes
            // asynchronously. Avoid replacing the clipboard too quickly.
            await Task.Delay(DestinationPasteDelay, cancellationToken);
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
            writer.DetachStream();
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
                "Windows could not set the image clipboard content.");
        }

        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private static async Task WaitForShortcutKeysReleasedAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (AnyShortcutKeyPressed())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Release Ctrl+V and any other modifier keys before pasting the stack.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static bool AnyShortcutKeyPressed()
    {
        return IsKeyPressed(VkControl)
            || IsKeyPressed(VkV)
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

        if (sent != (uint)inputs.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not inject the Ctrl+V shortcut.");
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
        // INPUT's native union is sized by MOUSEINPUT (32 bytes on 64-bit
        // Windows). Keeping every union member here is required even though
        // SnapStack only sends keyboard input; otherwise Marshal.SizeOf<INPUT>
        // is 32 instead of the required 40 and SendInput rejects the call.
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] INPUT[] inputs,
        int inputSize);
}
