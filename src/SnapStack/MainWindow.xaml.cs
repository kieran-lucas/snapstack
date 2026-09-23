using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SnapStack.Input;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;

namespace SnapStack;

public sealed partial class MainWindow : Window
{
    private const int CaptureHotKeyId = 1;
    private const int PasteHotKeyId = 2;
    private const int EndHotKeyId = 3;

    private const uint VirtualKeyX = 0x58;
    private const uint VirtualKeyZ = 0x5A;
    private const uint VirtualKeyV = 0x56;

    private readonly GlobalHotKeyService _hotKeyService;

    private bool _captureHotKeyEnabled;
    private bool _pasteHotKeyEnabled;
    private bool _endHotKeyEnabled;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        RootFrame.Navigate(typeof(MainPage));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotKeyService = new GlobalHotKeyService(hwnd);
        _hotKeyService.HotKeyPressed += HotKeyService_HotKeyPressed;

        Closed += MainWindow_Closed;
    }

    internal bool SetCaptureHotKeyEnabled(bool enabled, out string? error)
    {
        return SetHotKeyEnabled(
            CaptureHotKeyId,
            VirtualKeyZ,
            HotKeyModifiers.Control
                | HotKeyModifiers.NoRepeat,
            enabled,
            ref _captureHotKeyEnabled,
            out error);
    }

    internal bool SetPasteHotKeyEnabled(bool enabled, out string? error)
    {
        return SetHotKeyEnabled(
            PasteHotKeyId,
            VirtualKeyV,
            HotKeyModifiers.Control
                | HotKeyModifiers.NoRepeat,
            enabled,
            ref _pasteHotKeyEnabled,
            out error);
    }

    internal bool SetEndHotKeyEnabled(bool enabled, out string? error)
    {
        return SetHotKeyEnabled(
            EndHotKeyId,
            VirtualKeyX,
            HotKeyModifiers.Control
                | HotKeyModifiers.NoRepeat,
            enabled,
            ref _endHotKeyEnabled,
            out error);
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory, "Assets", "SnapStack.ico"));

        var displayArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary);

        var workArea = displayArea.WorkArea;
        var scale = GetDpiForWindow(hwnd) / 96d;

        var availableWidth = Math.Max(1, workArea.Width - 48);
        var availableHeight = Math.Max(1, workArea.Height - 48);

        var width = Math.Min(
            (int)Math.Round(900 * scale),
            availableWidth);

        var height = Math.Min(
            (int)Math.Round(720 * scale),
            availableHeight);

        appWindow.Resize(new SizeInt32(width, height));

        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        appWindow.Move(new PointInt32(x, y));

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        var background = Color.FromArgb(255, 7, 17, 31);
        var inactiveBackground = Color.FromArgb(255, 9, 22, 38);
        var hover = Color.FromArgb(255, 20, 52, 82);
        var pressed = Color.FromArgb(255, 31, 79, 125);
        var foreground = Colors.White;
        var inactiveForeground = Color.FromArgb(255, 117, 146, 175);

        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = inactiveBackground;
        titleBar.InactiveForegroundColor = inactiveForeground;

        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = inactiveBackground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private bool SetHotKeyEnabled(
        int id,
        uint virtualKey,
        HotKeyModifiers modifiers,
        bool enabled,
        ref bool state,
        out string? error)
    {
        error = null;

        if (enabled == state)
        {
            return true;
        }

        try
        {
            if (enabled)
            {
                _hotKeyService.Register(
                    id,
                    modifiers,
                    virtualKey);
            }
            else
            {
                _hotKeyService.Unregister(id);
            }

            state = enabled;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void HotKeyService_HotKeyPressed(
        object? sender,
        HotKeyPressedEventArgs e)
    {
        var app = (App)Application.Current;

        switch (e.Id)
        {
            case CaptureHotKeyId:
                app.RaiseCaptureHotKeyRequested(Capture.CaptureLatencyTrace.Now());
                break;

            case EndHotKeyId:
                app.RaiseEndHotKeyRequested();
                break;

            case PasteHotKeyId:
                // Ctrl+V is also the shortcut injected for every image. Release
                // our global registration before raising the event so those
                // injected keystrokes reach the foreground application instead
                // of recursively starting another SnapStack paste operation.
                SetPasteHotKeyEnabled(false, out _);
                app.RaisePasteHotKeyRequested();
                break;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _hotKeyService.HotKeyPressed -= HotKeyService_HotKeyPressed;
        _hotKeyService.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
