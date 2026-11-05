using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core.History;

/// <summary>One sheet as it was when the snapshot was taken.</summary>
public sealed class SnapshotSheet
{
    public string File { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Property label ("Sheet › Title", "Sheet · Identity Data › Drawn By") → value.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The whole sheet set at one moment: every sheet of every file that was open.</summary>
public sealed class SheetSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"Snapshot" (taken by the user) or "Before apply" (taken automatically).</summary>
    public string Kind { get; set; } = "Snapshot";
    public DateTime CreatedUtc { get; set; }
    public string User { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public List<SnapshotSheet> Sheets { get; set; } = new();

    public const string KindManual = "Snapshot";
    public const string KindBeforeApply = "Before apply";

    /// <summary>
    /// Captures the sheets of a sheet-index table, with the values read from the files (not pending edits):
    /// the standard sheet fields, the sheet's own parameters (Revit) and the title block attributes (AutoCAD).
    /// </summary>
    public static SheetSnapshot Capture(IEnumerable<TableRow> rows, string name, string kind)
    {
        var snap = new SheetSnapshot
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),
            Name = name,
            Kind = kind,
            CreatedUtc = DateTime.UtcNow,
            User = Environment.UserName,
        };
        foreach (var row in rows.Where(r => r.Values.ContainsKey(SheetFieldMap.NumberColumnId) || r.Values.ContainsKey(SheetFieldMap.ColumnId("Title"))))
        {
            var sheet = new SnapshotSheet
            {
                File = row.Document,
                ItemId = row.ItemId,
                Number = row.Values.GetValueOrDefault(SheetFieldMap.NumberColumnId)?.Value ?? "",
                Title = row.Values.GetValueOrDefault(SheetFieldMap.ColumnId("Title"))?.Value ?? "",
            };
            foreach (var (columnId, value) in row.Values)
                if (Tracked(columnId, value)) sheet.Values[columnId] = value.Value ?? "";
            snap.Sheets.Add(sheet);
        }
        snap.Files = snap.Sheets.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        return snap;
    }

    /// <summary>Standard fields, a Revit sheet's parameters, AutoCAD title block attributes, and revisions on sheet.</summary>
    private static bool Tracked(string columnId, PropertyValue value) =>
        columnId.StartsWith(SheetFieldMap.Group + " ›", StringComparison.Ordinal)
        || (value.Source != PropertySource.Derived && (columnId.StartsWith("Sheet · ", StringComparison.Ordinal) || columnId.StartsWith("Title Block ›", StringComparison.Ordinal)))
        || columnId.StartsWith("Revisions on Sheet ›", StringComparison.Ordinal)
        || columnId.StartsWith("Layout ›", StringComparison.Ordinal) && value.Name == "Layout Name";
}

public enum SheetDiffKind { Added, Removed, Changed }

public sealed record FieldDiff(string Field, string Before, string After);

/// <summary>How one sheet differs between a snapshot and now.</summary>
public sealed class SheetDiff
{
    public required SheetDiffKind Kind { get; init; }
    public required string File { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public List<FieldDiff> Fields { get; init; } = new();
}

public sealed class SnapshotComparison
{
    public List<SheetDiff> Sheets { get; } = new();
    /// <summary>Files in the snapshot that are not open now (not compared).</summary>
    public List<string> FilesNotOpen { get; } = new();
    /// <summary>Files open now that were not in the snapshot (not compared).</summary>
    public List<string> FilesNew { get; } = new();

    public int Added => Sheets.Count(s => s.Kind == SheetDiffKind.Added);
    public int Removed => Sheets.Count(s => s.Kind == SheetDiffKind.Removed);
    public int Changed => Sheets.Count(s => s.Kind == SheetDiffKind.Changed);

    /// <summary>
    /// Compares a snapshot with sheets read now. Only files present in both are compared. Sheets are
    /// matched by their id in the file (so a renumbered sheet shows as changed, not removed + added).
    /// </summary>
    public static SnapshotComparison Compare(SheetSnapshot before, SheetSnapshot now)
    {
        var result = new SnapshotComparison();
        var files = new HashSet<string>(before.Files.Intersect(now.Files, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        result.FilesNotOpen.AddRange(before.Files.Where(f => !files.Contains(f)));
        result.FilesNew.AddRange(now.Files.Where(f => !files.Contains(f)));

        static string Key(SnapshotSheet s) => s.File.ToUpperInvariant() + "\u0001" + s.ItemId;
        var old = before.Sheets.Where(s => files.Contains(s.File)).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var cur = now.Sheets.Where(s => files.Contains(s.File)).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var (key, s) in cur)
        {
            if (!old.TryGetValue(key, out var was))
            {
                result.Sheets.Add(new SheetDiff { Kind = SheetDiffKind.Added, File = s.File, Number = s.Number, Title = s.Title });
                continue;
            }
            var fields = new List<FieldDiff>();
            foreach (var field in was.Values.Keys.Union(s.Values.Keys))
            {
                string a = was.Values.GetValueOrDefault(field) ?? "";
                string b = s.Values.GetValueOrDefault(field) ?? "";
                // A field only one side has (column added to the reader later) is not a change.
                if (!was.Values.ContainsKey(field) || !s.Values.ContainsKey(field)) continue;
                if (a != b) fields.Add(new FieldDiff(Label(field), a, b));
            }
            // A standard field and the parameter it shows are the same change: keep one.
            fields = DropDuplicateFieldValues(fields);
            if (fields.Count > 0)
                result.Sheets.Add(new SheetDiff { Kind = SheetDiffKind.Changed, File = s.File, Number = s.Number, Title = s.Title, Fields = fields });
        }
        foreach (var (key, s) in old.Where(o => !cur.ContainsKey(o.Key)))
            result.Sheets.Add(new SheetDiff { Kind = SheetDiffKind.Removed, File = s.File, Number = s.Number, Title = s.Title });

        result.Sheets.Sort((x, y) =>
        {
            int c = string.Compare(x.File, y.File, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(x.Number, y.Number, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>"Sheet › Number" also changes when "Sheet Number" does: show the standard field only once per value pair.</summary>
    private static List<FieldDiff> DropDuplicateFieldValues(List<FieldDiff> fields)
    {
        var standard = fields.Where(f => !f.Field.Contains('›')).Select(f => (f.Before, f.After)).ToHashSet();
        return fields.Where(f => !f.Field.Contains('›') || !standard.Contains((f.Before, f.After))).ToList();
    }

    /// <summary>"Sheet › Title" → "Title"; "Sheet · Identity Data › Drawn By" → "Identity Data › Drawn By".</summary>
    private static string Label(string columnId)
    {
        if (columnId.StartsWith(SheetFieldMap.Group + " ›", StringComparison.Ordinal)) return SheetFieldMap.DisplayName(columnId[(SheetFieldMap.Group.Length + 2)..].Trim());
        return columnId.StartsWith("Sheet · ", StringComparison.Ordinal) ? columnId["Sheet · ".Length..] : columnId;
    }
}

/// <summary>Snapshots saved in %LOCALAPPDATA%\Nexus\snapshots, one JSON file each.</summary>
public static class SnapshotStore
{
    /// <summary>Automatic "before apply" snapshots kept (older ones are deleted).</summary>
    public const int KeepAutomatic = 40;

    public static string Folder => Path.Combine(NexusPaths.Root, "snapshots");

    public static void Save(SheetSnapshot snapshot)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, snapshot.Id + ".json"), JsonSerializer.Serialize(snapshot, Json.Options));
        if (snapshot.Kind == SheetSnapshot.KindBeforeApply) Prune();
    }

    /// <summary>Saved snapshots, newest first.</summary>
    public static List<SheetSnapshot> List()
    {
        var list = new List<SheetSnapshot>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SheetSnapshot>(File.ReadAllText(file), Json.Options);
                if (s is not null) list.Add(s);
            }
            catch (Exception ex)
            {
                HubLog.Warn($"Snapshot {Path.GetFileName(file)} could not be read.", ex);
            }
        }
        return list.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    public static void Delete(string id)
    {
        string path = Path.Combine(Folder, id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Prune()
    {
        foreach (var old in List().Where(s => s.Kind == SheetSnapshot.KindBeforeApply).Skip(KeepAutomatic))
            Delete(old.Id);
    }
}

/// <summary>One applied change, as recorded in the change log.</summary>
public sealed class ChangeLogEntry
{
    public DateTime TimeUtc { get; set; }
    public string User { get; set; } = "";
    public string Program { get; set; } = "";
    public string File { get; set; } = "";
    public string Item { get; set; } = "";
    public string Property { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Status { get; set; } = "";

    /// <summary>Local date and time, for lists.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string When => TimeUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Every change Nexus applied, appended to %LOCALAPPDATA%\Nexus\change-log.jsonl (one JSON object per line).</summary>
public static class ChangeLog
{
    public static string FilePath => Path.Combine(NexusPaths.Root, "change-log.jsonl");

    private static readonly JsonSerializerOptions Line = new(Json.Options) { WriteIndented = false };

    public static void Append(IEnumerable<ChangeLogEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(NexusPaths.Root);
            File.AppendAllLines(FilePath, entries.Select(e => JsonSerializer.Serialize(e, Line)));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not write the change log.", ex);
        }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public static List<ChangeLogEntry> Read(int max = 5000)
    {
        var list = new List<ChangeLogEntry>();
        if (!File.Exists(FilePath)) return list;
        foreach (var line in File.ReadLines(FilePath).Reverse().Take(max))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChangeLogEntry>(line, Line) is { } e) list.Add(e);
            }
            catch { /* skip a damaged line */ }
        }
        return list;
    }
}
