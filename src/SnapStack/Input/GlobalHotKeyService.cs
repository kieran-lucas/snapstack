using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnapStack.Input;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public sealed class GlobalHotKeyService : IDisposable
{
    private const uint WmHotKey = 0x0312;
    private const nuint SubclassId = 1;

    private readonly nint _windowHandle;
    private readonly SubclassProc _subclassProc;

    private int? _registeredId;
    private bool _disposed;

    public event EventHandler? HotKeyPressed;

    public GlobalHotKeyService(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _subclassProc = WindowProc;

        if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not install the SnapStack window message hook.");
        }
    }

    public void Register(int id, HotKeyModifiers modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registeredId is not null)
        {
            throw new InvalidOperationException("A SnapStack hotkey is already registered.");
        }

        if (!RegisterHotKey(_windowHandle, id, (uint)modifiers, virtualKey))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not register the SnapStack capture hotkey.");
        }

        _registeredId = id;
    }

    public void Unregister()
    {
        if (_registeredId is not int id)
        {
            return;
        }

        UnregisterHotKey(_windowHandle, id);
        _registeredId = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        _disposed = true;
    }

    private nint WindowProc(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint refData)
    {
        if (message == WmHotKey
            && _registeredId is int id
            && wParam == (nuint)id)
        {
            HotKeyPressed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint refData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint hWnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        nint hWnd,
        int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProc callback,
        nuint subclassId,
        nint refData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd,
        SubclassProc callback,
        nuint subclassId);
}
