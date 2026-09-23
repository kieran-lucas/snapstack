using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Storage;

namespace SnapStack.Capture;

// Enabled only for interactive measurement. Nothing is written during capture.
public sealed class CaptureLatencyTrace
{
    private static readonly object Gate = new();
    private static readonly List<CaptureLatencyTrace> Completed = [];

    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("SNAPSTACK_CAPTURE_BENCHMARK") == "1";

    public Guid Id { get; } = Guid.NewGuid();
    public long HotkeyDetected { get; }
    public long LaunchRequested { get; set; }
    public long LaunchReturned { get; set; }
    public long ProtocolActivated { get; set; }
    public long TokenRedeemed { get; set; }
    public long FileRead { get; set; }
    public long SessionStored { get; set; }
    public long ClipboardStarted { get; set; }
    public long ClipboardReady { get; set; }
    public long NextCaptureReady { get; set; }
    public string Outcome { get; set; } = "unknown";

    private CaptureLatencyTrace(long hotkeyDetected)
    {
        HotkeyDetected = hotkeyDetected;
    }

    public static CaptureLatencyTrace? Begin(long hotkeyDetected) =>
        Enabled ? new(hotkeyDetected) : null;

    public static long Now() => Stopwatch.GetTimestamp();

    public static void Complete(CaptureLatencyTrace? trace)
    {
        if (trace is null)
        {
            return;
        }

        lock (Gate)
        {
            Completed.Add(trace);
        }
    }

    public static async Task ExportAsync()
    {
        if (!Enabled)
        {
            return;
        }

        CaptureLatencyTrace[] snapshot;
        lock (Gate)
        {
            snapshot = Completed.ToArray();
        }

        var csv = new StringBuilder();
        csv.AppendLine("id,outcome,hotkey_to_launch_request_ms,launch_request_to_return_ms,hotkey_to_protocol_ms,protocol_to_token_ms,token_to_file_read_ms,file_read_to_session_ms,session_to_clipboard_start_ms,clipboard_publish_ms,protocol_to_next_ready_ms,hotkey_to_next_ready_ms");

        foreach (var trace in snapshot)
        {
            csv.Append(trace.Id).Append(',').Append(trace.Outcome).Append(',');
            AppendDuration(csv, trace.HotkeyDetected, trace.LaunchRequested);
            AppendDuration(csv, trace.LaunchRequested, trace.LaunchReturned);
            AppendDuration(csv, trace.HotkeyDetected, trace.ProtocolActivated);
            AppendDuration(csv, trace.ProtocolActivated, trace.TokenRedeemed);
            AppendDuration(csv, trace.TokenRedeemed, trace.FileRead);
            AppendDuration(csv, trace.FileRead, trace.SessionStored);
            AppendDuration(csv, trace.SessionStored, trace.ClipboardStarted);
            AppendDuration(csv, trace.ClipboardStarted, trace.ClipboardReady);
            AppendDuration(csv, trace.ProtocolActivated, trace.NextCaptureReady);
            AppendDuration(csv, trace.HotkeyDetected, trace.NextCaptureReady, last: true);
        }

        var path = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "capture-latency.csv");
        try
        {
            await File.WriteAllTextAsync(path, csv.ToString());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"SnapStack capture benchmark export failed: {exception}");
        }
    }

    private static void AppendDuration(
        StringBuilder csv,
        long start,
        long end,
        bool last = false)
    {
        if (start != 0 && end != 0)
        {
            csv.Append(Stopwatch.GetElapsedTime(start, end)
                .TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        csv.Append(last ? '\n' : ',');
    }
}
