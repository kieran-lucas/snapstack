using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapStack.Capture;

// Experimental single-output SDR fast path. The native probe rejects other
// topologies; App then retains the Snipping Tool implementation.
public sealed class NativeDxgiCaptureEngine : ICaptureEngine, IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _selectionRequested = new(false);
    private readonly BlockingCollection<EncodeJob> _encodeJobs = new();
    private readonly Thread _selectionThread;
    private readonly Thread _encoderThread;
    private IntPtr _handle;
    private CaptureLatencyTrace? _pendingTrace;
    private bool _pending;
    private bool _disposed;

    public event EventHandler<CaptureEngineResult>? CaptureCompleted;

    public bool IsCapturePending
    {
        get { lock (_gate) return _pending; }
    }

    private NativeDxgiCaptureEngine(IntPtr handle)
    {
        _handle = handle;
        _selectionThread = new Thread(SelectionLoop)
        {
            IsBackground = true,
            Name = "SnapStack native selection"
        };
        _encoderThread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = "SnapStack PNG encoder"
        };
        _selectionThread.Start();
        _encoderThread.Start();
    }

    public static NativeDxgiCaptureEngine? TryCreate()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (Native.GetAbiVersion() != 1)
                return null;
            handle = Native.Create(out var status);
            if (status != 0 || handle == IntPtr.Zero)
            {
                Debug.WriteLine($"SnapStack DXGI unavailable: 0x{status:X8}");
                if (handle != IntPtr.Zero) Native.Destroy(handle);
                return null;
            }
            return new NativeDxgiCaptureEngine(handle);
        }
        catch (Exception exception)
        {
            if (handle != IntPtr.Zero) Native.Destroy(handle);
            Debug.WriteLine($"SnapStack DXGI initialization failed: {exception}");
            return null;
        }
    }

    public Task<bool> BeginRectangleCaptureAsync(CaptureLatencyTrace? trace)
    {
        lock (_gate)
        {
            if (_disposed || _pending)
                return Task.FromResult(false);
            _pending = true;
            _pendingTrace = trace;
        }

        if (trace is not null) trace.LaunchRequested = CaptureLatencyTrace.Now();
        int status;
        try
        {
            status = Native.BeginSelection(_handle);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"SnapStack DXGI begin failed: {exception}");
            status = exception.HResult;
        }
        if (trace is not null) trace.LaunchReturned = CaptureLatencyTrace.Now();

        if (status != 0)
        {
            Native.Cancel(_handle);
            lock (_gate)
            {
                _pending = false;
                _pendingTrace = null;
            }
            Debug.WriteLine($"SnapStack DXGI begin returned 0x{status:X8}");
            return Task.FromResult(false);
        }

        _selectionRequested.Set();
        return Task.FromResult(true);
    }

    private void SelectionLoop()
    {
        while (true)
        {
            _selectionRequested.WaitOne();
            lock (_gate)
            {
                if (_disposed) return;
                if (!_pending) continue;
            }

            CaptureEngineResult result;
            try
            {
                result = WaitAndCrop();
            }
            catch (Exception exception)
            {
                Native.Cancel(_handle);
                result = CaptureEngineResult.Failed(exception.Message, _pendingTrace);
            }

            lock (_gate)
            {
                _pending = false;
                _pendingTrace = null;
                if (_disposed) return;
            }

            try { CaptureCompleted?.Invoke(this, result); }
            catch (Exception exception)
            {
                Debug.WriteLine($"SnapStack DXGI completion handler failed: {exception}");
            }
        }
    }

    private CaptureEngineResult WaitAndCrop()
    {
        var trace = _pendingTrace;
        var selection = new Native.Selection { AbiVersion = 1 };
        while (true)
        {
            var status = Native.WaitSelection(_handle, 250, ref selection);
            if (status == unchecked((int)0x80070102))
            {
                lock (_gate) { if (_disposed) return CaptureEngineResult.Cancelled(trace); }
                continue;
            }
            if (status != 0)
            {
                Native.Cancel(_handle);
                return CaptureEngineResult.Failed($"Selection failed (0x{status:X8}).", trace);
            }
            break;
        }

        if (trace is not null)
        {
            trace.OverlaySubmitted = selection.OverlaySubmittedAt;
            trace.MouseReleased = selection.ReleasedAt;
        }
        if (selection.Status != 0 || selection.Width == 0 || selection.Height == 0)
            return CaptureEngineResult.Cancelled(trace);

        int bytes;
        try { bytes = checked((int)(selection.Width * selection.Height * 4UL)); }
        catch (OverflowException)
        {
            Native.Cancel(_handle);
            return CaptureEngineResult.Failed("Capture region is too large.", trace);
        }

        var pixels = ArrayPool<byte>.Shared.Rent(bytes);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        int cropStatus;
        long submittedAt = 0, pixelsAt = 0;
        try
        {
            cropStatus = Native.Crop(_handle,
                selection.X, selection.Y, selection.Width, selection.Height,
                pin.AddrOfPinnedObject(), (nuint)bytes,
                out submittedAt, out pixelsAt);
        }
        finally { pin.Free(); }

        if (cropStatus != 0)
        {
            Native.Cancel(_handle);
            ArrayPool<byte>.Shared.Return(pixels);
            return CaptureEngineResult.Failed($"DXGI crop failed (0x{cropStatus:X8}).", trace);
        }

        if (trace is not null)
        {
            trace.CropSubmitted = submittedAt;
            trace.PixelsReady = pixelsAt;
        }
        var completion = new TaskCompletionSource<ReadOnlyMemory<byte>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _encodeJobs.Add(new EncodeJob(pixels, bytes, selection.Width,
            selection.Height, completion));
        var payload = new CapturePayload(ReadOnlyMemory<byte>.Empty,
            checked((int)selection.Width), checked((int)selection.Height),
            completion.Task);
        return CaptureEngineResult.Success(payload, trace);
    }

    private void EncodeLoop()
    {
        foreach (var job in _encodeJobs.GetConsumingEnumerable())
        {
            IntPtr encoded = IntPtr.Zero;
            var pin = GCHandle.Alloc(job.Pixels, GCHandleType.Pinned);
            try
            {
                var status = Native.EncodePng(pin.AddrOfPinnedObject(),
                    job.Width, job.Height, checked(job.Width * 4),
                    (nuint)job.Length, out encoded, out var encodedBytes);
                if (status != 0)
                    throw new InvalidOperationException($"PNG encode failed (0x{status:X8}).");
                var png = new byte[checked((int)encodedBytes)];
                Marshal.Copy(encoded, png, 0, png.Length);
                job.Completion.TrySetResult(png);
            }
            catch (Exception exception) { job.Completion.TrySetException(exception); }
            finally
            {
                pin.Free();
                if (encoded != IntPtr.Zero) Native.FreeBuffer(encoded);
                ArrayPool<byte>.Shared.Return(job.Pixels);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Native.CancelSelection(_handle);
        _selectionRequested.Set();
        _selectionThread.Join();
        _encodeJobs.CompleteAdding();
        _encoderThread.Join();
        Native.Destroy(_handle);
        _handle = IntPtr.Zero;
        _selectionRequested.Dispose();
        _encodeJobs.Dispose();
    }

    private sealed record EncodeJob(byte[] Pixels, int Length, uint Width,
        uint Height, TaskCompletionSource<ReadOnlyMemory<byte>> Completion);

    private static class Native
    {
        private const string Dll = "SnapStackCaptureNative.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct Selection
        {
            public uint AbiVersion;
            public int Status;
            public int X;
            public int Y;
            public uint Width;
            public uint Height;
            public long OverlaySubmittedAt;
            public long ReleasedAt;
        }

        [DllImport(Dll, EntryPoint = "SnapCore_GetAbiVersion", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint GetAbiVersion();

        [DllImport(Dll, EntryPoint = "SnapCore_Create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create(out int status);

        [DllImport(Dll, EntryPoint = "SnapCore_BeginSelection", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BeginSelection(IntPtr handle);

        [DllImport(Dll, EntryPoint = "SnapCore_WaitSelection", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WaitSelection(IntPtr handle, uint timeoutMs, ref Selection selection);

        [DllImport(Dll, EntryPoint = "SnapCore_CancelSelection", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CancelSelection(IntPtr handle);

        [DllImport(Dll, EntryPoint = "SnapCore_Crop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Crop(IntPtr handle, int x, int y,
            uint width, uint height, IntPtr destination, nuint destinationBytes,
            out long submittedAt, out long pixelsAt);

        [DllImport(Dll, EntryPoint = "SnapCore_Cancel", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Cancel(IntPtr handle);

        [DllImport(Dll, EntryPoint = "SnapCore_Destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);

        [DllImport(Dll, EntryPoint = "SnapCore_EncodePng", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodePng(IntPtr pixels, uint width, uint height,
            uint stride, nuint pixelsBytes, out IntPtr png, out nuint pngBytes);

        [DllImport(Dll, EntryPoint = "SnapCore_FreeBuffer", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FreeBuffer(IntPtr buffer);
    }
}
