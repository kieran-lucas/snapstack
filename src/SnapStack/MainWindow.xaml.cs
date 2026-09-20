using Microsoft.UI.Xaml;
using SnapStack.Input;

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
