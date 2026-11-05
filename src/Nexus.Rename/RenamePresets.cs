using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Rename;

/// <summary>Saved rule sets: one JSON file each in a folder (%APPDATA%\Nexus\rename-presets).</summary>
public sealed class RenamePresets
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public RenamePresets(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexus", "rename-presets");

    public List<string> Names()
    {
        if (!Directory.Exists(Folder)) return new List<string>();
        return Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(string name, RenameRules rules)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathOf(name), ToJson(rules));
    }

    public RenameRules? Load(string name) => File.Exists(PathOf(name)) ? FromJson(File.ReadAllText(PathOf(name))) : null;

    public void Delete(string name)
    {
        if (File.Exists(PathOf(name))) File.Delete(PathOf(name));
    }

    public static string ToJson(RenameRules rules) => JsonSerializer.Serialize(rules, Options);

    public static RenameRules FromJson(string json) => JsonSerializer.Deserialize<RenameRules>(json, Options) ?? new RenameRules();

    /// <summary>A copy (the window edits its own rules).</summary>
    public static RenameRules Clone(RenameRules rules) => FromJson(ToJson(rules));

    private string PathOf(string name)
    {
        // Windows' file name rules, the same on every machine.
        string safe = new string(name.Trim().Select(c => c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c).ToArray());
        return Path.Combine(Folder, (safe.Length == 0 ? "Preset" : safe) + ".json");
    }
}
