using System.Text.Json;
using Nexus.Agent;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>
/// Fallback property name -> palette category map, loaded from
/// property-categories.json next to the agent DLL. Modules can add entries.
/// </summary>
public sealed class CategoryMap
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static CategoryMap Load(string directory, AgentLog log)
    {
        var map = new CategoryMap();
        string path = Path.Combine(directory, "property-categories.json");
        try
        {
            if (!File.Exists(path))
            {
                log.Warn($"{path} not found; using built-in categories only.");
                return map;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("categories", out var cats))
                foreach (var cat in cats.EnumerateObject())
                    foreach (var name in cat.Value.EnumerateArray())
                        map.Add(name.GetString() ?? "", cat.Name);
        }
        catch (Exception ex)
        {
            log.Warn("Could not read property-categories.json.", ex);
        }
        return map;
    }

    public void Add(string propertyName, string category)
    {
        if (propertyName.Length > 0) _map.TryAdd(propertyName, category);
    }

    public string? CategoryFor(string propertyName) =>
        _map.TryGetValue(propertyName, out var c) ? c : null;
}
