using System.Globalization;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>Passed to a reader: options in, items and warnings out.</summary>
public sealed class ReadContext
{
    private readonly IReadOnlyDictionary<string, string> _options;
    private readonly ReaderDescriptor _descriptor;

    public ReadContext(ReaderDescriptor descriptor, IReadOnlyDictionary<string, string>? options,
        AgentLog log, CancellationToken cancellation)
    {
        _descriptor = descriptor;
        _options = options ?? new Dictionary<string, string>();
        Log = log;
        Cancellation = cancellation;
    }

    public AgentLog Log { get; }
    public CancellationToken Cancellation { get; }
    public List<DataItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Truncated { get; set; }

    public void Warn(string message)
    {
        // Keep result size bounded if something warns per object.
        if (Warnings.Count < 500) Warnings.Add(message);
        else if (Warnings.Count == 500) Warnings.Add("(further warnings suppressed)");
    }

    public string? GetString(string name)
    {
        if (_options.TryGetValue(name, out var v)) return v;
        return _descriptor.Options.FirstOrDefault(o => o.Name == name)?.Default;
    }

    public bool GetBool(string name)
    {
        var v = GetString(name);
        return v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    public int GetInt(string name, int fallback = 0)
    {
        var v = GetString(name);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
    }
}
