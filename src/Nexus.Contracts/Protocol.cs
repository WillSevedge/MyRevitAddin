namespace Nexus.Contracts;

/// <summary>Constants shared by the hub and every agent.</summary>
public static class Protocol
{
    /// <summary>Bumped when a breaking change is made to the message shapes.</summary>
    public const int Version = 1;

    /// <summary>Named pipe prefix. Full name: Nexus.Agent.{host}.{pid}</summary>
    public const string PipePrefix = "Nexus.Agent.";

    /// <summary>Largest frame either side will accept (256 MB).</summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The running hub listens here for one-line commands: "show", "refresh", "exit".
    /// Hosts use it to bring the hub forward; the build uses it to restart the hub.
    /// </summary>
    public const string HubControlPipe = "Nexus.Hub.Control";

    public static string PipeName(string hostKind, int processId) => $"{PipePrefix}{hostKind}.{processId}";
}

/// <summary>Request/response type names carried in <see cref="Envelope.Type"/>.</summary>
public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string ListDocuments = "listDocuments";
    public const string ListReaders = "listReaders";
    public const string Read = "read";
    public const string Write = "write";
    public const string Select = "select";
    /// <summary>Create PDFs of sheets of an open file.</summary>
    public const string ExportPdf = "exportPdf";
    public const string CreateSheets = "createSheets";
    public const string ListTools = "listTools";
    public const string CallTool = "callTool";

    public const string Result = "result";
    public const string Error = "error";
}

/// <summary>Error codes returned in <see cref="ErrorInfo.Code"/>.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "BadRequest";
    public const string UnknownMessage = "UnknownMessage";
    public const string ProtocolMismatch = "ProtocolMismatch";
    public const string HostBusy = "HostBusy";
    public const string NoDocument = "NoDocument";
    public const string DocumentNotFound = "DocumentNotFound";
    public const string ReaderNotFound = "ReaderNotFound";
    public const string NotImplemented = "NotImplemented";
    public const string Cancelled = "Cancelled";
    public const string Timeout = "Timeout";
    public const string Disconnected = "Disconnected";
    public const string InternalError = "InternalError";
}
