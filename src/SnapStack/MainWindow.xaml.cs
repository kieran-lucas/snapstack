using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SnapStack.Input;
using Windows.Graphics;
using Windows.UI;

namespace SnapStack;

public sealed partial class MainWindow : Window
{
    private const int CaptureHotKeyId = 1;
    private const int PasteHotKeyId = 2;

    private const uint VirtualKeyS = 0x53;
    private const uint VirtualKeyV = 0x56;

    private readonly GlobalHotKeyService _hotKeyService;

    private bool _captureHotKeyEnabled;
    private bool _pasteHotKeyEnabled;

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
            VirtualKeyS,
            enabled,
            ref _captureHotKeyEnabled,
            out error);
    }

    internal bool SetPasteHotKeyEnabled(bool enabled, out string? error)
    {
        return SetHotKeyEnabled(
            PasteHotKeyId,
            VirtualKeyV,
            enabled,
            ref _pasteHotKeyEnabled,
            out error);
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        var displayArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary);

        var workArea = displayArea.WorkArea;

        var width = Math.Min(
            1180,
            Math.Max(720, workArea.Width - 64));

        var height = Math.Min(
            860,
            Math.Max(640, workArea.Height - 64));

        appWindow.Resize(new SizeInt32(width, height));

        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        appWindow.Move(new PointInt32(x, y));

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;

        var background = Color.FromArgb(255, 8, 11, 20);
        var inactiveBackground = Color.FromArgb(255, 11, 15, 26);
        var hover = Color.FromArgb(255, 33, 43, 68);
        var pressed = Color.FromArgb(255, 52, 45, 91);
        var foreground = Colors.White;
        var inactiveForeground = Color.FromArgb(255, 142, 153, 176);

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
                    HotKeyModifiers.Control
                        | HotKeyModifiers.Shift
                        | HotKeyModifiers.NoRepeat,
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
                app.RaiseCaptureHotKeyRequested();
                break;

            case PasteHotKeyId:
                app.RaisePasteHotKeyRequested();
                break;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _hotKeyService.HotKeyPressed -= HotKeyService_HotKeyPressed;
        _hotKeyService.Dispose();
    }
}
