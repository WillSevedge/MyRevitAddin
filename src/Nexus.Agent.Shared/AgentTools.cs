using System.Globalization;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>
/// A command for AI assistants (Nexus.Mcp): query or change the open document. Called on the host thread, so it
/// may use the host API directly. Throw <see cref="ToolException"/> for a request that cannot be done (unknown
/// layer, bad handle...): the assistant gets the message and can correct itself.
/// </summary>
public interface IAgentTool<in TDoc> where TDoc : class
{
    ToolDescriptor Descriptor { get; }

    /// <summary>Host thread only. Returns any JSON-serializable object (or a <see cref="ToolOutput"/>).</summary>
    object? Call(TDoc document, ToolArgs args, AgentLog log, CancellationToken ct);
}

/// <summary>A result with a short summary and, optionally, a picture.</summary>
public sealed class ToolOutput
{
    public object? Data { get; init; }
    public string? Summary { get; init; }
    public byte[]? Png { get; init; }
}

/// <summary>The request cannot be done as asked; the message tells the assistant why.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

public sealed class ToolRegistry<TDoc> where TDoc : class
{
    // Modules add tools on the host thread after start-up while the pipe thread may be listing them.
    private readonly Dictionary<string, IAgentTool<TDoc>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Register(IAgentTool<TDoc> tool)
    {
        lock (_lock) _tools[tool.Descriptor.Name] = tool;
    }

    public IAgentTool<TDoc>? Find(string name)
    {
        lock (_lock) return _tools.TryGetValue(name, out var t) ? t : null;
    }

    public List<ToolDescriptor> Descriptors()
    {
        lock (_lock) return _tools.Values.Select(t => t.Descriptor).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Reads a tool's arguments forgivingly (numbers as strings, missing values) with clear errors.</summary>
public sealed class ToolArgs
{
    private readonly JsonElement _root;

    public ToolArgs(JsonElement? root) => _root = root is { ValueKind: JsonValueKind.Object } r ? r : default;

    public JsonElement Root => _root;

    public bool Has(string name) => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public JsonElement? Get(string name) => Has(name) ? _root.GetProperty(name) : null;

    public string? String(string name) => Get(name) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public string RequiredString(string name) =>
        String(name) is { Length: > 0 } s ? s : throw new ToolException($"'{name}' is required.");

    public double? Number(string name) => Get(name) is { } v ? ToNumber(v, name) : null;

    public int Int(string name, int fallback, int min = int.MinValue, int max = int.MaxValue) =>
        Number(name) is { } d ? (int)Math.Max(min, Math.Min(max, Math.Round(d))) : fallback;

    public bool Bool(string name, bool fallback) => Get(name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback,
        }
        : fallback;

    public List<string> Strings(string name)
    {
        if (Get(name) is not { } v) return new List<string>();
        if (v.ValueKind == JsonValueKind.String) return new List<string> { v.GetString()! };
        if (v.ValueKind != JsonValueKind.Array) throw new ToolException($"'{name}' must be a list.");
        return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList();
    }

    /// <summary>Each item of a list argument, as its own <see cref="ToolArgs"/>.</summary>
    public List<ToolArgs> Objects(string name)
    {
        if (Get(name) is not { ValueKind: JsonValueKind.Array } v) return new List<ToolArgs>();
        return v.EnumerateArray().Select(e => new ToolArgs(e)).ToList();
    }

    public ToolArgs? Object(string name) => Get(name) is { ValueKind: JsonValueKind.Object } v ? new ToolArgs(v) : null;

    /// <summary>[x, y] or [x, y, z] (or {"x":…,"y":…}); null when missing.</summary>
    public double[]? Point(string name) => Get(name) is { } v ? ToPoint(v, name) : null;

    public double[] RequiredPoint(string name) => Point(name) ?? throw new ToolException($"'{name}' is required ([x, y] or [x, y, z]).");

    public List<double[]> Points(string name)
    {
        if (Get(name) is not { ValueKind: JsonValueKind.Array } v) return new List<double[]>();
        return v.EnumerateArray().Select(p => ToPoint(p, name)).ToList();
    }

    private static double[] ToPoint(JsonElement v, string name)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var n = v.EnumerateArray().Select(e => ToNumber(e, name)).ToArray();
            if (n.Length is 2 or 3) return n.Length == 2 ? new[] { n[0], n[1], 0.0 } : n;
        }
        else if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("x", out var x) && v.TryGetProperty("y", out var y))
        {
            double z = v.TryGetProperty("z", out var zz) ? ToNumber(zz, name) : 0;
            return new[] { ToNumber(x, name), ToNumber(y, name), z };
        }
        throw new ToolException($"'{name}' must be a point: [x, y] or [x, y, z].");
    }

    private static double ToNumber(JsonElement v, string name) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new ToolException($"'{name}' must be a number."),
    };
}

/// <summary>Small helpers to write JSON schemas for tool descriptors.</summary>
public static class Schema
{
    public static JsonElement Of(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>A tool with its descriptor built from a name, description and JSON schema (for add-in modules).</summary>
public abstract class AgentTool<TDoc> : IAgentTool<TDoc> where TDoc : class
{
    protected AgentTool(string name, string title, string description, string schema, bool readOnly, bool destructive = false) =>
        Descriptor = new ToolDescriptor
        {
            Name = name, Title = title, Description = description, InputSchema = Schema.Of(schema),
            ReadOnly = readOnly, Destructive = destructive,
        };

    public ToolDescriptor Descriptor { get; }
    public abstract object? Call(TDoc document, ToolArgs args, AgentLog log, CancellationToken ct);
}
