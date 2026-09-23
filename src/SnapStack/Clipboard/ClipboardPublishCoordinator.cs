using Microsoft.UI.Dispatching;
using SnapStack.Core;
using System.Runtime.InteropServices;

namespace SnapStack.Clipboard;

// One logical worker prepares complete immutable stack snapshots. When captures
// arrive faster than clipboard publication, only the newest queued snapshot is
// built; waiters for superseded snapshots complete with that newer publication.
internal sealed class ClipboardPublishCoordinator
{
    private readonly ClipboardStackService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);
    private readonly Task _worker;

    private WorkItem? _pending;
    private WorkItem? _latestRequested;
    private bool _signalOutstanding;

    public ClipboardPublishCoordinator(
        ClipboardStackService service,
        DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _worker = Task.Run(ProcessAsync);
    }

    public Task<string?> Enqueue(
        IReadOnlyList<CapturedImage> captures,
        bool force = false)
    {
        if (captures.Count == 0)
        {
            throw new ArgumentException("A capture stack is required.", nameof(captures));
        }

        var last = captures[^1];
        lock (_gate)
        {
            if (!force
                && _latestRequested is { } latest
                && latest.Count == captures.Count
                && latest.LastId == last.Id
                && (latest.PublishedSequence == 0
                    || latest.PublishedSequence == GetClipboardSequenceNumber()))
            {
                return latest.Completion.Task;
            }

            var request = new WorkItem(
                captures.ToArray(),
                captures.Count,
                last.Id);

            if (_pending is { } previous)
            {
                request.Superseded.Add(previous);
                request.Superseded.AddRange(previous.Superseded);
            }

            _pending = request;
            _latestRequested = request;

            if (!_signalOutstanding)
            {
                _signalOutstanding = true;
                _pendingSignal.Release();
            }

            return request.Completion.Task;
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            await _pendingSignal.WaitAsync().ConfigureAwait(false);

            WorkItem request;
            lock (_gate)
            {
                request = _pending!;
                _pending = null;
                _signalOutstanding = false;
            }

            string? error = null;
            PreparedClipboardStack? prepared = null;
            var published = false;

            try
            {
                prepared = await _service.PrepareAsync(request.Captures)
                    .ConfigureAwait(false);
                request.PublishedSequence =
                    await PublishOnUiThreadAsync(prepared).ConfigureAwait(false);
                published = true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                if (prepared is not null && !published)
                {
                    await ClipboardStackService.DiscardAsync(prepared)
                        .ConfigureAwait(false);
                }
            }

            if (published)
            {
                try
                {
                    await ClipboardStackService.CleanupAsync(prepared!)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup must never invalidate a completed publication.
                }
            }

            if (error is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_latestRequested, request))
                    {
                        _latestRequested = null;
                    }
                }
            }

            request.Completion.TrySetResult(error);
            foreach (var superseded in request.Superseded)
            {
                superseded.Completion.TrySetResult(error);
            }
        }
    }

    private Task<uint> PublishOnUiThreadAsync(PreparedClipboardStack prepared)
    {
        var completed = new TaskCompletionSource<uint>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    ClipboardStackService.PublishPrepared(prepared);
                    completed.TrySetResult(GetClipboardSequenceNumber());
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            }))
        {
            completed.TrySetException(
                new InvalidOperationException("The SnapStack UI dispatcher is unavailable."));
        }

        return completed.Task;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private sealed class WorkItem(
        IReadOnlyList<CapturedImage> captures,
        int count,
        Guid lastId)
    {
        public IReadOnlyList<CapturedImage> Captures { get; } = captures;
        public int Count { get; } = count;
        public Guid LastId { get; } = lastId;
        public uint PublishedSequence { get; set; }
        public List<WorkItem> Superseded { get; } = [];
        public TaskCompletionSource<string?> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
