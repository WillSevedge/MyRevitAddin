using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>One edited cell: a property of a table row and the value the user typed.</summary>
public sealed class CellEdit
{
    public CellEdit(TableRow row, string columnId, string newValue)
    {
        Row = row;
        ColumnId = columnId;
        NewValue = newValue;
        Property = row.Values[columnId];
    }

    public TableRow Row { get; }
    public string ColumnId { get; }
    public PropertyValue Property { get; }
    public string NewValue { get; }
    public string OldValue => Property.Value ?? "";

    public string DocumentId => Row.Source.Result.DocumentId;
    public int ProcessId => Row.Source.Host.ProcessId;

    public PropertyChange ToChange() => new()
    {
        OwnerId = string.IsNullOrEmpty(Property.OwnerId) ? Row.ItemId : Property.OwnerId,
        PropertyId = Property.Id,
        PropertyName = Property.Name,
        Source = Property.Source,
        Value = NewValue,
        ExpectedRawValue = Property.RawValue,
    };
}

public static class Editing
{
    /// <summary>Value kinds that refer to other objects or are not plain values; Nexus cannot edit them yet.</summary>
    private static readonly HashSet<string> ReferenceStorageTypes = new(StringComparer.Ordinal)
    {
        "ElementId", "ObjectId", "Handle", "COM", "Collection", "Extents3d", "Matrix3d",
    };

    /// <summary>Why a table cell cannot be edited, or null if it can.</summary>
    public static string? Blocker(TableRow row, string columnId)
    {
        if (!row.Values.TryGetValue(columnId, out var p)) return "This item does not have this property.";
        var host = row.Source.Host;
        if (!host.Features.Contains(AgentFeatures.Write)) return $"{host.Product} does not support editing yet.";
        if (p.Source == PropertySource.Derived) return "Computed by Nexus (not a parameter).";
        if (p.IsReadOnly) return p.ReadOnlyReason ?? "Read-only.";
        if (p.StorageType is { } st && ReferenceStorageTypes.Contains(st))
            return "Values that refer to other objects (materials, types, levels, layers by id, styles...) cannot be edited yet.";
        return null;
    }

    /// <summary>Same text, treating null and empty as equal.</summary>
    public static bool SameValue(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    /// <summary>
    /// Groups edits into one write request per host process and document.
    /// If the same property of the same object was edited twice (e.g. it appears in two
    /// results), the last edit wins.
    /// </summary>
    public static List<(int ProcessId, WriteRequest Request, List<CellEdit> Edits)> Plan(IEnumerable<CellEdit> edits)
    {
        var plans = new List<(int, WriteRequest, List<CellEdit>)>();
        foreach (var group in edits.GroupBy(e => (e.ProcessId, e.DocumentId)))
        {
            var unique = group
                .GroupBy(e => (e.ToChange().OwnerId, e.Property.Id ?? e.Property.Name))
                .Select(g => g.Last())
                .ToList();
            plans.Add((group.Key.ProcessId, new WriteRequest
            {
                DocumentId = group.Key.DocumentId,
                Changes = unique.Select(e => e.ToChange()).ToList(),
            }, unique));
        }
        return plans;
    }
}
