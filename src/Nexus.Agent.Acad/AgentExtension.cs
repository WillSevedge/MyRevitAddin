using System.Reflection;
using Nexus.Agent;
using Nexus.Agent.Acad.Modules;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Agent.Acad.Readers;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Nexus.Agent.Acad.AgentExtension))]
[assembly: CommandClass(typeof(Nexus.Agent.Acad.AgentCommands))]

namespace Nexus.Agent.Acad;

/// <summary>
/// AutoCAD-family entry point (loaded by the Nexus.bundle autoloader in AutoCAD,
/// Civil 3D and other verticals). Starts the pipe agent; loads vertical modules
/// once AutoCAD has finished starting.
/// </summary>
public sealed class AgentExtension : IExtensionApplication
{
    internal static AgentExtension? Instance { get; private set; }

    internal AgentLog Log { get; } = new("acad");
    internal AgentServer<Document>? Server { get; private set; }
    internal HostInfo? Host { get; private set; }
    internal AcadRibbon? Ribbon => _ribbon;

    private AcadDispatcher? _dispatcher;
    private AcadRibbon? _ribbon;
    private ReaderRegistry<Document>? _readers;
    private ObjectPropertyReader? _objects;
    private ToolRegistry<Document>? _tools;
    private readonly List<IAcadPropertyWriter> _moduleWriters = new();

    public void Initialize()
    {
        Instance = this;
        // 2024 (.NET Framework): load our own copies of the libraries we ship if the program has others.
        Compat.ResolveDependenciesFrom(System.IO.Path.GetDirectoryName(typeof(Compat).Assembly.Location) ?? AppContext.BaseDirectory);
        // Started by the hub in AutoCAD's Core Console to make PDFs (Print & PDF): no agent, no ribbon
        // (Nexus.Agent.Acad.Console does the work there).
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HeadlessJob.EnvironmentVariable))) return;
        StartAgent();
    }

    /// <summary>Kept out of <see cref="Initialize"/> so the Core Console never loads the ribbon's libraries.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void StartAgent()
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(AgentExtension).Assembly.Location) ?? AppContext.BaseDirectory;
            string year = typeof(AgentExtension).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "HostYear")?.Value ?? "";
            string build = "";
            try { build = $"{AcApp.Version} (ACADVER {AcApp.GetSystemVariable("ACADVER")})"; } catch { /* ignored */ }

            Host = HostInfoFactory.Create(HostKinds.AutoCAD, "AutoCAD", year, build, Log);
            Log.Info($"Starting in {Host.Product} {year}, {build}, {Host.Runtime}");

            var journal = new ProbeJournal(Log);
            _objects = new ObjectPropertyReader(journal, CategoryMap.Load(dir, Log), Log);
            _readers = new ReaderRegistry<Document>();
            _readers.Register(new SheetsReader());
            _readers.Register(new LayoutsReader(_objects));

            _tools = Tools.CadTools.Create();
            _dispatcher = new AcadDispatcher(Log);
            Server = new AgentServer<Document>(Host, _dispatcher.Queue, new AcadDocumentProvider(), _readers, Log,
                writer: new AcadWriter(_moduleWriters), selector: new AcadSelector(), tools: _tools);
            try
            {
                _ribbon = new AcadRibbon(Log, () => Server?.Status, () => Host);
                Server.StatusChanged += _ribbon.OnStatusChanged;
            }
            catch (System.Exception ex)
            {
                // The agent still works without the ribbon (e.g. AutoCAD Core Console).
                Log.Warn("Could not set up the Nexus ribbon tab.", ex);
            }
            Server.Start();

            // Vertical products finish loading their own modules after us; detect on first idle.
            AcApp.Idle += OnFirstIdle;
        }
        catch (System.Exception ex)
        {
            Log.Error("Nexus agent failed to start.", ex);
        }
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnFirstIdle;
        try
        {
            if (_readers is null || _objects is null || _tools is null || Host is null || Server is null) return;
            string dir = Path.GetDirectoryName(typeof(AgentExtension).Assembly.Location) ?? AppContext.BaseDirectory;
            var loaded = ModuleLoader.LoadAll(dir, new AcadModuleContext(Log, _readers, _objects, _moduleWriters, _tools));
            if (loaded.Count == 0) return;

            Host.Modules = loaded.Select(m => m.Name).ToList();
            var product = loaded.Select(m => m.ProductName).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (product is not null) Host.Product = product;
            Server.UpdateHost(Host);
        }
        catch (System.Exception ex)
        {
            Log.Error("Loading modules failed.", ex);
        }
    }

    public void Terminate()
    {
        try
        {
            Server?.Dispose();
            _dispatcher?.Dispose();
            _ribbon?.Dispose();
        }
        catch (System.Exception ex)
        {
            Log.Error("Error during shutdown.", ex);
        }
    }
}

public sealed class AgentCommands
{
    /// <summary>NEXUSSTATUS: print the agent's connection status.</summary>
    [CommandMethod("NEXUSSTATUS", CommandFlags.Modal)]
    public void Status()
    {
        var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null) return;
        var app = AgentExtension.Instance;
        var s = app?.Server?.Status;
        if (s is null)
        {
            ed.WriteMessage("\nNexus agent is not running. Log folder: " + NexusPaths.LogsDir);
            return;
        }
        ed.WriteMessage($"\nNexus agent: {s.State}" +
                        $"\n  Host: {app!.Host?.Name}  Modules: {string.Join(", ", app.Host?.Modules ?? new())}" +
                        $"\n  Pipe: {s.PipeName}" +
                        $"\n  Hub connections: {s.Clients}  Requests: {s.RequestsHandled}" +
                        (s.LastError is null ? "" : $"\n  Last error: {s.LastError}") +
                        $"\n  Log: {app.Log.FilePath}\n");
    }

    /// <summary>NEXUS: status window with a button to show the Nexus hub (same as the ribbon button).</summary>
    [CommandMethod("NEXUS", CommandFlags.Modal)]
    public void ShowWindow()
    {
        var app = AgentExtension.Instance;
        if (app?.Ribbon is { } ribbon)
        {
            ribbon.ShowStatusWindow();
            return;
        }
        var error = HubControl.Show(app?.Log ?? new AgentLog("acad"));
        if (error is not null) AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + error);
    }
}
