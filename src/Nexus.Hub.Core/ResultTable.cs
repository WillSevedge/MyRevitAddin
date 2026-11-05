using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>One read result plus where it came from.</summary>
public sealed class ResultSource
{
    public required HostInfo Host { get; init; }
    public required string DocumentTitle { get; init; }
    public required ReadResult Result { get; init; }
    /// <summary>Whatever the caller wants to find this source again by (the hub stores its result run).</summary>
    public object? Tag { get; init; }
}

public sealed record ColumnKey(string Group, string Name)
{
    public string Id => Group + " › " + Name;

    /// <summary>What users see: standard sheet fields by their one display name (Sheet Number, Sheet Name...).</summary>
    public string Label => Group == SheetFieldMap.Group ? SheetFieldMap.DisplayName(Name) : Name;
}

public sealed class TableRow
{
    public string Host { get; init; } = "";
    public string Document { get; init; } = "";
    public string Reader { get; init; } = "";
    public string Path { get; init; } = "";
    public int Depth { get; init; }
    public string ItemType { get; init; } = "";
    public string Item { get; init; } = "";
    public string Key { get; init; } = "";
    public string ItemId { get; init; } = "";
    public required ResultSource Source { get; init; }
    public Dictionary<string, PropertyValue> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Flattens read results into rows (one per item, children included) and
/// "Group › Property" columns, for display and export.
/// </summary>
public sealed class ResultTable
{
    public List<ColumnKey> Columns { get; } = new();
    public List<TableRow> Rows { get; } = new();

    /// <summary>Column id → column, for every column in <see cref="Columns"/>.</summary>
    public Dictionary<string, ColumnKey> ColumnsById { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Flattens results into rows and columns. With <paramref name="sheetFields"/>, adds the standard
    /// "Sheet › …" columns first; each refers to the same property as its source column, so editing
    /// either edits the parameter or attribute behind it.
    /// </summary>
    public static ResultTable Build(IEnumerable<ResultSource> sources, bool includeChildren = true, SheetFieldMap? sheetFields = null)
    {
        var table = new ResultTable();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in sources)
            foreach (var item in s.Result.Items)
                table.AddItem(s, item, item.Name, 0, includeChildren, known);

        foreach (var c in table.Columns) table.ColumnsById[c.Id] = c;
        if (sheetFields is not null) table.AddSheetFields(sheetFields);
        return table;
    }

    private void AddSheetFields(SheetFieldMap map)
    {
        var fieldColumns = map.Fields.Select(f => new ColumnKey(SheetFieldMap.Group, f.Name)).ToList();
        foreach (var row in Rows)
            foreach (var (field, value) in map.Match(row, ColumnsById))
                row.Values[SheetFieldMap.ColumnId(field)] = value;

        // Only fields found in at least one row, in the map's order, before every other column.
        var used = fieldColumns.Where(c => Rows.Any(r => r.Values.ContainsKey(c.Id))).ToList();
        Columns.InsertRange(0, used);
        foreach (var c in used) ColumnsById[c.Id] = c;
    }

    private void AddItem(ResultSource s, DataItem item, string path, int depth, bool includeChildren, HashSet<string> known)
    {
        var row = new TableRow
        {
            Host = s.Host.Name,
            Document = s.DocumentTitle,
            Reader = s.Result.ReaderId,
            Path = path,
            Depth = depth,
            ItemType = item.ItemType,
            Item = item.Name,
            Key = item.Key ?? "",
            ItemId = item.Id,
            Source = s,
        };

        foreach (var group in item.Groups)
        {
            var seenInGroup = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in group.Properties)
            {
                // Revit allows two parameters with the same name (e.g. a shared and a project one).
                string name = prop.Name;
                int n = seenInGroup.TryGetValue(name, out var c) ? c + 1 : 1;
                seenInGroup[name] = n;
                if (n > 1) name = $"{name} ({n})";

                var key = new ColumnKey(group.Name, name);
                if (known.Add(key.Id)) Columns.Add(key);
                row.Values[key.Id] = prop;
            }
        }
        Rows.Add(row);

        if (!includeChildren) return;
        foreach (var child in item.Children)
            AddItem(s, child, path + " / " + child.Name, depth + 1, true, known);
    }
}
