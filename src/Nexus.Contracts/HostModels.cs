namespace Nexus.Contracts;

/// <summary>Well-known host kinds. Strings, so new verticals need no contract change.</summary>
public static class HostKinds
{
    public const string Revit = "Revit";
    public const string AutoCAD = "AutoCAD";
}

/// <summary>Describes one running host process (one Revit.exe or acad.exe).</summary>
public sealed class HostInfo
{
    /// <summary>Revit or AutoCAD (the agent family).</summary>
    public string HostKind { get; set; } = "";
    /// <summary>What is actually running, e.g. "Revit", "AutoCAD", "Civil 3D".</summary>
    public string Product { get; set; } = "";
    /// <summary>Marketing year, e.g. "2026".</summary>
    public string Version { get; set; } = "";
    /// <summary>Full build string reported by the host.</summary>
    public string Build { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTime ProcessStartUtc { get; set; }
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Runtime { get; set; } = "";
    /// <summary>Optional modules loaded by the agent, e.g. "Civil3D".</summary>
    public List<string> Modules { get; set; } = new();
    /// <summary>Optional capabilities, see <see cref="AgentFeatures"/>.</summary>
    public List<string> Features { get; set; } = new();
    public string? LogFile { get; set; }

    /// <summary>For people: "Revit 2026", "Civil 3D 2026".</summary>
    public string Name => $"{Product} {Version}";

    /// <summary>For logs: includes the process id.</summary>
    public string DisplayName => $"{Product} {Version} (pid {ProcessId})";
}

/// <summary>Written by each agent to %LOCALAPPDATA%\Nexus\agents\{pid}.json.</summary>
public sealed class AgentRegistration
{
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string PipeName { get; set; } = "";
    public HostInfo Host { get; set; } = new();
    public DateTime RegisteredUtc { get; set; }
}

public sealed class HelloResponse
{
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public HostInfo Host { get; set; } = new();
}

public sealed class DocumentInfo
{
    /// <summary>Stable for the life of the host session; use it in read requests.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Path { get; set; }
    public bool IsActive { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsModified { get; set; }
    /// <summary>Host-specific extras (e.g. IsWorkshared, IsLinked, IsFamily).</summary>
    public Dictionary<string, string> Extra { get; set; } = new();
}

public sealed class ListDocumentsResponse
{
    public List<DocumentInfo> Documents { get; set; } = new();
}
