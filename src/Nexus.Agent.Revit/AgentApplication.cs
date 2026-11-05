using Nexus.Agent;
using Nexus.Agent.Revit.Readers;
using Nexus.Contracts;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

/// <summary>
/// Revit entry point. Starts the named-pipe agent, adds the Nexus ribbon panel,
/// and marshals hub requests onto Revit's main thread through an ExternalEvent.
/// </summary>
public sealed class AgentApplication : IExternalApplication
{
    internal static AgentApplication? Instance { get; private set; }

    internal AgentLog Log { get; } = new("revit");
    internal AgentServer<Document>? Server { get; private set; }
    internal HostInfo? Host { get; private set; }

    private RevitDispatcher? _dispatcher;
    private StatusRibbon? _ribbon;

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;
        var startup = System.Diagnostics.Stopwatch.StartNew();
        // 2024 (.NET Framework): load our own copies of the libraries we ship if the program has others.
        Compat.ResolveDependenciesFrom(System.IO.Path.GetDirectoryName(typeof(Compat).Assembly.Location) ?? AppContext.BaseDirectory);
        try
        {
            var app = application.ControlledApplication;
            Host = HostInfoFactory.Create(HostKinds.Revit, "Revit", app.VersionNumber,
                $"{app.SubVersionNumber} ({app.VersionBuild}) {app.Product}", Log);
            Log.Info($"Starting in {app.VersionName} {app.SubVersionNumber}, build {app.VersionBuild}, {Host.Runtime}");

            try
            {
                _ribbon = new StatusRibbon(application, Log);
            }
            catch (Exception ex)
            {
                // The agent still works without the ribbon.
                Log.Error("Could not create the ribbon panel.", ex);
            }

            _dispatcher = new RevitDispatcher(Log);
            Server = new AgentServer<Document>(Host, _dispatcher.Queue, new RevitDocumentProvider(_dispatcher),
                RevitReaders.CreateRegistry(), Log, writer: new RevitWriter(), selector: new RevitSelector(_dispatcher),
                pdf: new RevitPdfExporter(), sheets: new RevitSheetCreator());
            if (_ribbon is not null) Server.StatusChanged += _ribbon.OnStatusChanged;
            Server.Start();
            // What Nexus adds to Revit's start (the ribbon and the pipe server): see the agent log.
            Log.Info($"Nexus add-in ready in {startup.ElapsedMilliseconds} ms");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error("Nexus agent failed to start.", ex);
            // Succeed anyway so Revit does not show an add-in failure dialog on every start;
            // the ribbon/status command and log explain what happened.
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            Server?.Dispose();
            _dispatcher?.Dispose();
            _ribbon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown.", ex);
        }
        return Result.Succeeded;
    }
}
