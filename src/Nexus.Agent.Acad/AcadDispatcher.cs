using Nexus.Agent;
using Autodesk.AutoCAD.ApplicationServices;
using WinForms = System.Windows.Forms;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nexus.Agent.Acad;

/// <summary>
/// Marshals pipe requests onto AutoCAD's main thread. A hidden control created
/// on the main thread receives BeginInvoke calls; work only runs when AutoCAD
/// is in the application context (no command in progress). While a command is
/// running we retry every 250 ms until the request's start timeout expires.
/// </summary>
internal sealed class AcadDispatcher : IDisposable
{
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(3);

    private readonly AgentLog _log;
    private readonly WinForms.Control _control;
    private readonly WinForms.Timer _retry;

    /// <summary>Must be constructed on AutoCAD's main thread (IExtensionApplication.Initialize).</summary>
    public AcadDispatcher(AgentLog log)
    {
        _log = log;
        _control = new WinForms.Control();
        _ = _control.Handle; // force handle creation on this (main) thread
        _retry = new WinForms.Timer { Interval = 250 };
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            DrainOnMainThread();
        };
        Queue = new WorkQueue(RequestDrain, log);
    }

    public WorkQueue Queue { get; }

    private void RequestDrain()
    {
        if (_control.IsDisposed) throw new ObjectDisposedException("AutoCAD is shutting down.");
        _control.BeginInvoke(new Action(DrainOnMainThread));
    }

    private void DrainOnMainThread()
    {
        try
        {
            DocumentCollection docs = AcApp.DocumentManager;
            if (!docs.IsApplicationContext)
            {
                // A command is running (or AutoCAD is prompting). Try again shortly.
                _retry.Start();
                return;
            }
            Queue.Drain(DrainBudget);
        }
        catch (Exception ex)
        {
            // Never let anything escape into AutoCAD.
            _log.Error("Unexpected error while draining the request queue.", ex);
        }
    }

    public void Dispose()
    {
        Queue.FailAll("AutoCAD is shutting down.");
        try { _retry.Dispose(); } catch { /* ignored */ }
        try { _control.Dispose(); } catch { /* ignored */ }
    }
}
