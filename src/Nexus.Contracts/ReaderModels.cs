namespace Nexus.Contracts;

/// <summary>Describes one registered reader. The hub builds its UI from these.</summary>
public sealed class ReaderDescriptor
{
    /// <summary>Unique id, e.g. "revit.sheets", "acad.layouts", "civil3d.objects".</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Data domain, e.g. "Sheets", "Project", "Layouts".</summary>
    public string Domain { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>False for registered placeholders that return NotImplemented.</summary>
    public bool IsImplemented { get; set; } = true;
    public List<ReaderOption> Options { get; set; } = new();
}

public sealed class ReaderOption
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>"bool", "int" or "string".</summary>
    public string Type { get; set; } = "string";
    public string? Default { get; set; }
    public string? Description { get; set; }

    public static ReaderOption Bool(string name, string displayName, bool @default, string? description = null) =>
        new() { Name = name, DisplayName = displayName, Type = "bool", Default = @default ? "true" : "false", Description = description };

    public static ReaderOption Int(string name, string displayName, int @default, string? description = null) =>
        new() { Name = name, DisplayName = displayName, Type = "int", Default = @default.ToString(System.Globalization.CultureInfo.InvariantCulture), Description = description };
}

public sealed class ListReadersResponse
{
    public List<ReaderDescriptor> Readers { get; set; } = new();
}

public sealed class ReadRequest
{
    /// <summary>A <see cref="DocumentInfo.Id"/>, or "active" for the active document.</summary>
    public string DocumentId { get; set; } = "active";
    public string ReaderId { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new();
    /// <summary>
    /// The caller fills <see cref="PropertyValue.RawValue"/> from <see cref="PropertyValue.Value"/> when it is
    /// missing, so the agent leaves out raw values that only repeat the value (most text parameters): a smaller
    /// result to send and parse. Agents that do not know the flag send everything.
    /// </summary>
    public bool Compact { get; set; }
}

public sealed class ReadResult
{
    public string ReaderId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public DateTime ReadUtc { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>Raw values equal to the value were left out (<see cref="ReadRequest.Compact"/>); see <see cref="Compaction"/>.</summary>
    public bool Compacted { get; set; }
    public List<DataItem> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>True when a limit (e.g. max objects) cut the result short.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// One thing that was read: a sheet, a layout, a paper space object, project info...
/// Properties are grouped the way the host's properties palette groups them.
/// </summary>
public sealed class DataItem
{
    /// <summary>Host-unique id (Revit UniqueId, AutoCAD handle, ...).</summary>
    public string Id { get; set; } = "";
    /// <summary>Kind of item, e.g. "Sheet", "Layout", "BlockReference", "Alignment".</summary>
    public string ItemType { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Natural matching key (sheet number, layout name, ...), if any.</summary>
    public string? Key { get; set; }
    public List<PropertyGroup> Groups { get; set; } = new();
    public List<DataItem> Children { get; set; } = new();
}

public sealed class PropertyGroup
{
    public string Name { get; set; } = "";
    public List<PropertyValue> Properties { get; set; } = new();

    public PropertyGroup() { }
    public PropertyGroup(string name) => Name = name;
}

public enum PropertySource
{
    Unknown,
    /// <summary>Revit built-in parameter.</summary>
    BuiltIn,
    /// <summary>Revit project parameter.</summary>
    Project,
    /// <summary>Revit shared parameter.</summary>
    Shared,
    /// <summary>Revit family parameter (e.g. title block type).</summary>
    Family,
    /// <summary>AutoCAD block attribute.</summary>
    Attribute,
    /// <summary>AutoCAD dynamic block property.</summary>
    DynamicBlock,
    /// <summary>Read through the managed .NET API.</summary>
    Managed,
    /// <summary>Read through the COM/ActiveX API.</summary>
    Com,
    /// <summary>Computed by the agent (e.g. current revision summary).</summary>
    Derived,
    /// <summary>System variable / drawing setting.</summary>
    Setting,
    /// <summary>Whether something is assigned to the item (e.g. a revision shown on a sheet): Yes/No.</summary>
    Assignment,
}

public sealed class PropertyValue
{
    public string Name { get; set; } = "";
    /// <summary>Stable identifier where the host has one (BuiltInParameter name, shared GUID, COM DISPID, ...).</summary>
    public string? Id { get; set; }
    public PropertySource Source { get; set; }
    /// <summary>Host storage/data type, e.g. "String", "Double", "ElementId", "Int32".</summary>
    public string? StorageType { get; set; }
    /// <summary>Spec/data type label, e.g. "Length", "Text", "Yes/No".</summary>
    public string? DataType { get; set; }
    /// <summary>Display units label, e.g. "Feet and fractional inches".</summary>
    public string? Units { get; set; }
    /// <summary>Value as the user sees it (formatted).</summary>
    public string? Value { get; set; }
    /// <summary>Raw value in host internal units, invariant culture.</summary>
    public string? RawValue { get; set; }
    public bool HasValue { get; set; } = true;
    /// <summary>Left out of the JSON when false (most values): the reader's default is false too.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsReadOnly { get; set; }
    public string? ReadOnlyReason { get; set; }
    /// <summary>
    /// Host id of the object that owns this property, when it is not the item itself
    /// (e.g. a sheet's title block). Edits are sent to this object.
    /// </summary>
    public string? OwnerId { get; set; }
}

/// <summary>
/// Compact reads: raw values equal to the shown value are left out by the agent and put back by the hub.
/// A value that really has no raw value is sent as <see cref="NoRawValue"/>, so the two cases stay apart
/// (writers skip the "changed meanwhile" check when there is no raw value).
/// </summary>
public static class Compaction
{
    public const string NoRawValue = "\u0000";

    /// <summary>Agent side.</summary>
    public static void Compact(ReadResult result)
    {
        foreach (var item in result.Items)
            foreach (var group in item.Groups)
                foreach (var p in group.Properties)
                {
                    if (p.RawValue is null) { if (p.Value is not null) p.RawValue = NoRawValue; }
                    else if (string.Equals(p.RawValue, p.Value, StringComparison.Ordinal)) p.RawValue = null;
                }
        result.Compacted = true;
    }

    /// <summary>Hub side: undoes <see cref="Compact"/>.</summary>
    public static void Expand(ReadResult result)
    {
        if (!result.Compacted) return;
        foreach (var item in result.Items)
            foreach (var group in item.Groups)
                foreach (var p in group.Properties)
                {
                    if (p.RawValue is null) p.RawValue = p.Value;
                    else if (p.RawValue == NoRawValue) p.RawValue = null;
                }
        result.Compacted = false;
    }
}
