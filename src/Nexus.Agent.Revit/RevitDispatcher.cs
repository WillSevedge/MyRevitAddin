using Nexus.Agent;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

/// <summary>
/// Marshals pipe requests onto Revit's main thread. Requests are queued in a
/// <see cref="WorkQueue"/>; raising the ExternalEvent makes Revit call
/// <see cref="Execute"/> on its API thread when it is idle, where the queue is drained.
/// </summary>
internal sealed class RevitDispatcher : IExternalEventHandler, IDisposable
{
    // How long one Execute call may keep draining before yielding back to Revit.
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(3);

    private readonly AgentLog _log;
    private readonly ExternalEvent _event;

    /// <summary>Must be constructed in a valid Revit API context (OnStartup).</summary>
    public RevitDispatcher(AgentLog log)
    {
        _log = log;
        Queue = new WorkQueue(RequestDrain, log);
        _event = ExternalEvent.Create(this);
    }

    public WorkQueue Queue { get; }

    /// <summary>The UIApplication for the Execute call currently running (null otherwise).</summary>
    public UIApplication? Current { get; private set; }

    private void RequestDrain()
    {
        var result = _event.Raise();
        if (result is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
            throw new InvalidOperationException($"Revit did not accept the external event ({result}).");
    }

    public void Execute(UIApplication app)
    {
        try
        {
            Current = app;
            Queue.Drain(DrainBudget);
        }
        catch (Exception ex)
        {
            // Never let anything escape into Revit.
            _log.Error("Unexpected error while draining the request queue.", ex);
        }
        finally
        {
            Current = null;
        }
    }

    public string GetName() => "Nexus request dispatcher";

    public void Dispose()
    {
        Queue.FailAll("Revit is shutting down.");
        try { _event.Dispose(); } catch { /* ignored */ }
    }
}
