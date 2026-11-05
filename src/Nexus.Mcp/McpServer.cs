using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Mcp;

/// <summary>Where the server finds the Nexus add-ins (the running programs; a fixed set in tests).</summary>
public interface IAgentSource
{
    IReadOnlyList<AgentRegistration> Discover();
    Task<AgentClient> ConnectAsync(AgentRegistration agent, CancellationToken ct);
}

/// <summary>The programs running now with the Nexus add-in (their registration files).</summary>
public sealed class RunningAgents : IAgentSource
{
    public IReadOnlyList<AgentRegistration> Discover() => AgentDiscovery.Discover();
    public Task<AgentClient> ConnectAsync(AgentRegistration agent, CancellationToken ct) =>
        AgentClient.ConnectAsync(agent.PipeName, TimeSpan.FromSeconds(5), ct);
}

/// <summary>
/// A Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0). Each program's tools (AutoCAD's
/// query_entities, create_entities…) are offered as "autocad_query_entities"… and forwarded to the add-in running
/// in that program; plus "nexus_status" to see what is open. The tool list is remembered between runs, so the
/// assistant knows the CAD tools even before AutoCAD starts, and is re-announced when a program starts or stops.
/// </summary>
public sealed class McpServer
{
    public const string ServerVersion = "0.1.0";
    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private const string Instructions =
        "Nexus connects you to AutoCAD (and AutoCAD Civil 3D / Plant 3D) running on this computer through the Nexus add-in. " +
        "For anything in AutoCAD use these tools only: do not write .lsp or .scr files, run PowerShell or scripts, or automate AutoCAD " +
        "through COM; the tools draw, change and read the drawing directly, each as one undo step, without prompts in AutoCAD. " +
        "Start with nexus_status to see which programs and drawings are open, then autocad_drawing_info. Find entities with " +
        "autocad_query_entities (paged), read details with autocad_get_entities, and see the drawing with autocad_view_image. " +
        "Drawing and changes are one undo step each in AutoCAD. Coordinates are drawing units, angles degrees. " +
        "Every tool works on the active drawing unless you pass \"document\" (a drawing's title from nexus_status). " +
        "In Civil 3D also use autocad_civil3d_* (alignments, station/offset, surfaces and elevations, pipe networks, COGO points, corridors); " +
        "in Plant 3D autocad_plant3d_* (project, objects with tag/line number/size/spec, changing their data).";

    private readonly IAgentSource _agents;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<int, AgentClient> _clients = new();
    private readonly SemaphoreSlim _clientsLock = new(1, 1);
    private readonly string? _cacheFile;
    private Dictionary<string, List<ToolDescriptor>> _toolsByKind = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The tool names each running session offers (Civil 3D's differ from plain AutoCAD's).</summary>
    private readonly Dictionary<int, HashSet<string>> _toolsBySession = new();
    private bool _initialized;
    private string _lastAnnounced = "";

    public McpServer(IAgentSource agents, Stream input, Stream output, string? cacheFile = "")
    {
        _agents = agents;
        _input = input;
        _output = output;
        _cacheFile = cacheFile == "" ? Path.Combine(NexusPaths.Root, "mcp-tools.json") : cacheFile;
        _toolsByKind = LoadCache();
        // AutoCAD's tools are known up front: the assistant learns them even before AutoCAD runs.
        if (!_toolsByKind.ContainsKey(HostKinds.AutoCAD)) _toolsByKind[HostKinds.AutoCAD] = CadToolCatalog.All.ToList();
    }

    /// <summary>How often the server looks for programs that started or stopped (and announces tool changes).</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(_input, new UTF8Encoding(false));
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WatchAsync(watch.Token);
        var running = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line is null) break; // the client closed stdin: done
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("\"initialize\"", StringComparison.Ordinal))
            {
                // Answered before anything that follows it.
                await HandleLineAsync(line, ct).ConfigureAwait(false);
                continue;
            }
            // Requests are answered concurrently (a long drawing must not block ping).
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => HandleLineAsync(line, ct), CancellationToken.None));
        }
        // Answer what was asked before the client closed.
        await Task.WhenAll(running).ConfigureAwait(false);
        watch.Cancel();
        foreach (var c in _clients.Values) await c.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ JSON-RPC

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? id = null;
        try
        {
            var message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Not a JSON object.");
            id = message["id"]?.DeepClone();
            string method = message["method"]?.GetValue<string>() ?? "";
            var parameters = message["params"] as JsonObject;
            if (id is null)
            {
                // A notification: nothing to answer.
                if (method == "notifications/initialized") _initialized = true;
                return;
            }
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => await ListToolsAsync(ct).ConfigureAwait(false),
                "tools/call" => await CallToolAsync(parameters, ct).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new RpcException(-32601, $"Method '{method}' is not supported."),
            };
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            int code = ex switch { RpcException r => r.Code, JsonException => -32700, _ => -32603 };
            if (ex is not RpcException) HubLog.Warn("MCP request failed.", ex);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
            }).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        string asked = p?["protocolVersion"]?.GetValue<string>() ?? SupportedVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = SupportedVersions.Contains(asked) ? asked : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
            ["serverInfo"] = new JsonObject { ["name"] = "nexus", ["title"] = "Nexus (AutoCAD, Civil 3D, Plant 3D)", ["version"] = ServerVersion },
            ["instructions"] = Instructions,
        };
    }

    // ------------------------------------------------------------------ tools

    private async Task<JsonObject> ListToolsAsync(CancellationToken ct)
    {
        await RefreshToolsAsync(ct).ConfigureAwait(false);
        var tools = new JsonArray { StatusTool() };
        foreach (var (kind, descriptors) in _toolsByKind.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var d in descriptors)
                tools.Add(ToMcp(kind, d));
        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject StatusTool() => new()
    {
        ["name"] = "nexus_status",
        ["title"] = "Open programs and drawings",
        ["description"] = "Which programs are running with the Nexus add-in (AutoCAD, Civil 3D, Plant 3D, Revit), their process ids, and the drawings/models open in each (the active one marked).",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    /// <summary>The agent's tool as an MCP tool: prefixed name, and "document"/"session" added to its arguments.</summary>
    private static JsonObject ToMcp(string kind, ToolDescriptor d)
    {
        var schema = JsonNode.Parse(d.InputSchema.ValueKind == JsonValueKind.Undefined ? "{\"type\":\"object\"}" : d.InputSchema.GetRawText()) as JsonObject
                     ?? new JsonObject { ["type"] = "object" };
        schema["type"] = "object";
        if (schema["properties"] is not JsonObject props) schema["properties"] = props = new JsonObject();
        props["document"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Which open drawing (its title from nexus_status). Default: the active drawing.",
        };
        props["session"] = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = "Process id of the program (from nexus_status) when it is open more than once.",
        };
        return new JsonObject
        {
            ["name"] = $"{Prefix(kind)}_{d.Name}",
            ["title"] = d.Title,
            ["description"] = d.Description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["title"] = d.Title,
                ["readOnlyHint"] = d.ReadOnly,
                ["destructiveHint"] = d.Destructive,
                ["openWorldHint"] = false,
            },
        };
    }

    private static string Prefix(string hostKind) => hostKind.ToLowerInvariant().Replace(" ", "");

    private async Task<JsonObject> CallToolAsync(JsonObject? p, CancellationToken ct)
    {
        string name = p?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "tools/call needs a tool name.");
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            if (name == "nexus_status") return Text(await StatusAsync(ct).ConfigureAwait(false));

            int split = name.IndexOf('_');
            if (split <= 0) throw new RpcException(-32602, $"Unknown tool '{name}'.");
            string prefix = name.Substring(0, split), tool = name.Substring(split + 1);
            var candidates = _agents.Discover()
                .Where(a => Prefix(a.Host.HostKind) == prefix && a.Host.Features.Contains(AgentFeatures.Tools)).ToList();
            if (candidates.Count == 0)
            {
                string program = _toolsByKind.Keys.FirstOrDefault(k => Prefix(k) == prefix) ?? prefix;
                return Error($"{program} is not running with the Nexus add-in. Ask the user to open it (and a drawing), then try again.");
            }

            string? document = args["document"]?.GetValue<string>();
            int? session = args["session"] is JsonValue sv && sv.TryGetValue<int>(out int pid) ? pid : null;
            args.Remove("document");
            args.Remove("session");

            // Sessions that offer this tool (Civil 3D tools only in Civil 3D…), when known.
            if (session is null)
            {
                if (_toolsBySession.Count == 0) await RefreshToolsAsync(ct).ConfigureAwait(false);
                List<AgentRegistration> offering;
                lock (_toolsBySession)
                    offering = candidates.Where(a => _toolsBySession.TryGetValue(a.Host.ProcessId, out var names) && names.Contains(tool)).ToList();
                if (offering.Count > 0) candidates = offering;
                else if (candidates.All(a => _toolsBySession.ContainsKey(a.Host.ProcessId)))
                    return Error($"No running program offers '{name}'. It belongs to {(tool.StartsWith("civil3d_", StringComparison.Ordinal) ? "Civil 3D" : tool.StartsWith("plant3d_", StringComparison.Ordinal) ? "Plant 3D" : "a newer Nexus add-in")}: open that program with the drawing, then try again.");
            }
            var agent = await PickAsync(candidates, session, document, ct).ConfigureAwait(false);
            var client = await ClientAsync(agent, ct).ConfigureAwait(false);
            var result = await client.CallToolAsync(new CallToolRequest
            {
                Tool = tool,
                Document = document,
                Arguments = JsonSerializer.SerializeToElement(args),
            }, ct).ConfigureAwait(false);
            return ToMcpResult(result);
        }
        catch (AgentRequestException ex)
        {
            return Error(ex.Message);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            return Error("Lost the connection to the program: " + ex.Message);
        }
    }

    /// <summary>The program to use: the session asked for, the one with the document open, or the only/first one.</summary>
    private async Task<AgentRegistration> PickAsync(List<AgentRegistration> candidates, int? session, string? document, CancellationToken ct)
    {
        if (session is { } pid)
            return candidates.FirstOrDefault(a => a.Host.ProcessId == pid)
                   ?? throw new AgentRequestException(new ErrorInfo(ErrorCodes.BadRequest, $"No session {pid}. Sessions: {string.Join(", ", candidates.Select(c => c.Host.ProcessId))}."));
        if (candidates.Count == 1 || string.IsNullOrWhiteSpace(document)) return candidates[0];
        foreach (var a in candidates)
        {
            var docs = await (await ClientAsync(a, ct).ConfigureAwait(false)).ListDocumentsAsync(ct).ConfigureAwait(false);
            if (docs.Any(d => d.Title.IndexOf(document!, StringComparison.OrdinalIgnoreCase) >= 0)) return a;
        }
        return candidates[0];
    }

    private static JsonObject ToMcpResult(CallToolResult r)
    {
        var content = new JsonArray();
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(r.Summary)) text.Append(r.Summary);
        if (r.Data is { } data)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(data.GetRawText());
        }
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
        if (r.ImagePng is { Length: > 0 } png) content.Add(new JsonObject { ["type"] = "image", ["data"] = png, ["mimeType"] = "image/png" });
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "Done." });
        return new JsonObject { ["content"] = content, ["isError"] = r.IsError };
    }

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = false,
    };

    private static JsonObject Error(string text) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = true,
    };

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        var agents = _agents.Discover();
        if (agents.Count == 0)
            return "No program is running with the Nexus add-in. Open AutoCAD (or Civil 3D, Plant 3D, Revit) with Nexus installed.";
        var lines = new List<object>();
        foreach (var a in agents)
        {
            List<DocumentInfo> docs;
            string? error = null;
            try { docs = await (await ClientAsync(a, ct).ConfigureAwait(false)).ListDocumentsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is AgentRequestException or IOException or TimeoutException)
            {
                docs = new List<DocumentInfo>();
                error = ex.Message;
            }
            lines.Add(new
            {
                program = a.Host.Product,
                version = a.Host.Version,
                session = a.Host.ProcessId,
                tools = a.Host.Features.Contains(AgentFeatures.Tools) ? $"{Prefix(a.Host.HostKind)}_*" : "none (update its Nexus add-in)",
                documents = docs.Select(d => new { title = d.Title, active = d.IsActive, path = d.Path }),
                error,
            });
        }
        return JsonSerializer.Serialize(lines, Json.Indented);
    }

    // ------------------------------------------------------------------ programs and their tools

    private async Task<AgentClient> ClientAsync(AgentRegistration agent, CancellationToken ct)
    {
        await _clientsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int pid = agent.Host.ProcessId;
            if (_clients.TryGetValue(pid, out var existing))
            {
                try
                {
                    await existing.PingAsync(ct).ConfigureAwait(false);
                    return existing;
                }
                catch
                {
                    _clients.Remove(pid);
                    try { await existing.DisposeAsync().ConfigureAwait(false); } catch { /* gone */ }
                }
            }
            var client = await _agents.ConnectAsync(agent, ct).ConfigureAwait(false);
            _clients[pid] = client;
            return client;
        }
        finally
        {
            _clientsLock.Release();
        }
    }

    /// <summary>Asks each running program kind for its tools; kinds not running keep their remembered tools.</summary>
    private async Task RefreshToolsAsync(CancellationToken ct)
    {
        var tools = new Dictionary<string, List<ToolDescriptor>>(_toolsByKind, StringComparer.OrdinalIgnoreCase);
        var running = _agents.Discover().Where(a => a.Host.Features.Contains(AgentFeatures.Tools)).ToList();
        lock (_toolsBySession)
            foreach (var pid in _toolsBySession.Keys.Where(pid => running.All(a => a.Host.ProcessId != pid)).ToList())
                _toolsBySession.Remove(pid);
        foreach (var group in running.GroupBy(a => a.Host.HostKind))
        {
            // Every session of the program: plain AutoCAD, Civil 3D and Plant 3D each add their own tools.
            var merged = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
            foreach (var agent in group)
            {
                try
                {
                    var client = await ClientAsync(agent, ct).ConfigureAwait(false);
                    var listed = await client.ListToolsAsync(ct).ConfigureAwait(false);
                    foreach (var t in listed) merged.TryAdd(t.Name, t);
                    lock (_toolsBySession) _toolsBySession[agent.Host.ProcessId] = new HashSet<string>(listed.Select(t => t.Name));
                }
                catch (Exception ex) when (ex is AgentRequestException or IOException or TimeoutException)
                {
                    HubLog.Warn($"MCP: could not list the tools of {agent.Host.Product} ({agent.Host.ProcessId}).", ex);
                }
            }
            // Keep the core tools known even when only some sessions answered.
            if (merged.Count > 0)
            {
                if (group.Key == HostKinds.AutoCAD)
                    foreach (var t in CadToolCatalog.All) merged.TryAdd(t.Name, t);
                tools[group.Key] = merged.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            }
        }
        _toolsByKind = tools;
        SaveCache();
    }

    /// <summary>Announces a changed tool list (a program with a newer add-in started, or the first one ever).</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(WatchInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!_initialized) continue;
            try
            {
                await RefreshToolsAsync(ct).ConfigureAwait(false);
                string signature = string.Join("|", _toolsByKind.OrderBy(k => k.Key).SelectMany(k => k.Value.Select(t => k.Key + ":" + t.Name)));
                if (_lastAnnounced.Length > 0 && signature != _lastAnnounced)
                    await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }).ConfigureAwait(false);
                _lastAnnounced = signature;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HubLog.Warn("MCP: watching the programs failed.", ex);
            }
        }
    }

    private Dictionary<string, List<ToolDescriptor>> LoadCache()
    {
        try
        {
            if (_cacheFile is not null && File.Exists(_cacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, List<ToolDescriptor>>>(File.ReadAllText(_cacheFile), Json.Options)
                           is { } cached ? new Dictionary<string, List<ToolDescriptor>>(cached, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            HubLog.Warn("MCP: could not read the remembered tools.", ex);
        }
        return new Dictionary<string, List<ToolDescriptor>>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache()
    {
        if (_cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(_toolsByKind, Json.Options));
        }
        catch (Exception ex)
        {
            HubLog.Warn("MCP: could not remember the tools.", ex);
        }
    }
}

internal sealed class RpcException : Exception
{
    public RpcException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}
