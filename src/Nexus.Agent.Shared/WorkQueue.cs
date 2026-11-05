using System.Collections.Concurrent;
using System.Diagnostics;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>
/// Host-neutral request queue used to marshal work from pipe threads to the host
/// thread. The host supplies a "please call Drain() on your thread soon" callback
/// (Revit: ExternalEvent.Raise; AutoCAD: Control.BeginInvoke) and calls
/// <see cref="Drain"/> from its main thread.
/// </summary>
public sealed class WorkQueue : IHostDispatcher
{
    private const int Pending = 0, Started = 1, Abandoned = 2;

    private sealed class WorkItem
    {
        public required Func<object?> Run { get; init; }
        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int State;
    }

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly Action _requestDrain;
    private readonly AgentLog _log;

    public WorkQueue(Action requestDrain, AgentLog log)
    {
        _requestDrain = requestDrain;
        _log = log;
    }

    public int PendingCount => _queue.Count;

    public async Task<T> InvokeAsync<T>(Func<T> work, TimeSpan startTimeout, CancellationToken ct)
    {
        var item = new WorkItem { Run = () => work() };
        _queue.Enqueue(item);

        try
        {
            _requestDrain();
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref item.State, Abandoned, Pending) == Pending)
                throw new AgentException(ErrorCodes.HostBusy, "The host refused the request: " + ex.Message, ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(startTimeout, timeoutCts.Token);
        var finished = await Task.WhenAny(item.Completion.Task, delay).ConfigureAwait(false);

        if (finished != item.Completion.Task)
        {
            // Only abandon if the host has not picked it up; if it already started we must wait.
            if (Interlocked.CompareExchange(ref item.State, Abandoned, Pending) == Pending)
            {
                if (ct.IsCancellationRequested)
                    throw new AgentException(ErrorCodes.Cancelled, "The request was cancelled before it started.");
                throw new AgentException(ErrorCodes.HostBusy,
                    $"The host did not become available within {startTimeout.TotalSeconds:0}s. " +
                    "Close any open dialogs or finish the active command, then try again.");
            }
        }
        timeoutCts.Cancel();

        object? result = await item.Completion.Task.ConfigureAwait(false);
        return (T)result!;
    }

    /// <summary>
    /// Runs queued work. MUST be called on the host thread. Stops after
    /// <paramref name="budget"/> so the host UI stays responsive, and asks for
    /// another drain if work remains.
    /// </summary>
    public void Drain(TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        while (_queue.TryDequeue(out var item))
        {
            if (Interlocked.CompareExchange(ref item.State, Started, Pending) != Pending)
                continue; // abandoned by a timeout

            try
            {
                item.Completion.TrySetResult(item.Run());
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }

            if (sw.Elapsed > budget && !_queue.IsEmpty)
            {
                try { _requestDrain(); }
                catch (Exception ex) { _log.Warn("Could not request another drain.", ex); }
                return;
            }
        }
    }

    /// <summary>Fail everything still queued (used at shutdown).</summary>
    public void FailAll(string message)
    {
        while (_queue.TryDequeue(out var item))
        {
            if (Interlocked.CompareExchange(ref item.State, Abandoned, Pending) == Pending)
                item.Completion.TrySetException(new AgentException(ErrorCodes.HostBusy, message));
        }
    }
}
