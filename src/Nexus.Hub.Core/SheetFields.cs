using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>
/// One standard sheet field ("Number", "Title", "Revision"...) and how to find it in each program:
/// a Revit parameter id, or a title block attribute tag / property name.
/// </summary>
public sealed class SheetField
{
    public string Name { get; set; } = "";
    /// <summary>Tried in order; the first rule that matches a property of the row wins.</summary>
    public List<FieldRule> Rules { get; set; } = new();
}

public sealed class FieldRule
{
    /// <summary>Only for this host kind ("Revit", "AutoCAD"); null for any.</summary>
    public string? Host { get; set; }
    /// <summary>The property's group must contain this text (case-insensitive); null for any group.</summary>
    public string? Group { get; set; }
    /// <summary>Exact property id (e.g. Revit's SHEET_NUMBER).</summary>
    public string? Id { get; set; }
    /// <summary>Property names / attribute tags; compared ignoring case, spaces and punctuation.</summary>
    public List<string> Names { get; set; } = new();
}

/// <summary>
/// The standard sheet columns shown first in the Sheets dataset, for Revit sheets and AutoCAD layouts alike.
/// Users can extend the tag lists in %LOCALAPPDATA%\Nexus\sheet-fields.json.
/// </summary>
public sealed class SheetFieldMap
{
    public const string Group = "Sheet";
    public const string NumberField = "Number";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<SheetField> Fields { get; set; } = new();

    public static string ColumnId(string field) => new ColumnKey(Group, field).Id;

    /// <summary>
    /// The one name users see for a standard field, whatever program the sheet is in (Revit's names, which
    /// most people know). Column ids keep the short field name, so saved Excel links are not affected.
    /// </summary>
    public static string DisplayName(string field) => field switch
    {
        NumberField => "Sheet Number",
        "Title" => "Sheet Name",
        "Issue Date" => "Sheet Issue Date",
        _ => field,
    };
    public static string NumberColumnId => ColumnId(NumberField);

    /// <summary>The user's map (created from the defaults the first time).</summary>
    public static SheetFieldMap LoadOrCreate()
    {
        string path = Path.Combine(NexusPaths.Root, "sheet-fields.json");
        try
        {
            if (File.Exists(path))
            {
                var map = JsonSerializer.Deserialize<SheetFieldMap>(File.ReadAllText(path), FileOptions);
                if (map is { Fields.Count: > 0 })
                {
                    bool moved = MoveLayoutNameToTitle(map);
                    if (AddMissingFields(map) | moved)
                        File.WriteAllText(path, JsonSerializer.Serialize(map, FileOptions));
                    return map;
                }
            }
            var defaults = Default();
            Directory.CreateDirectory(NexusPaths.Root);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, FileOptions));
            return defaults;
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Could not read {path}; using the built-in sheet fields.", ex);
            return Default();
        }
    }

    /// <summary>
    /// Maps written before the layout name moved from Sheet Number to Sheet Name: move it.
    /// Returns true when the map changed.
    /// </summary>
    public static bool MoveLayoutNameToTitle(SheetFieldMap map)
    {
        static bool IsLayoutName(FieldRule r) =>
            string.Equals(r.Host, HostKinds.AutoCAD, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Group, "Layout", StringComparison.OrdinalIgnoreCase)
            && r.Names.Any(n => Normalize(n) == "LAYOUTNAME");

        var number = map.Fields.FirstOrDefault(f => f.Name == NumberField);
        var rule = number?.Rules.FirstOrDefault(IsLayoutName);
        if (number is null || rule is null) return false;
        number.Rules.Remove(rule);
        var title = map.Fields.FirstOrDefault(f => f.Name == "Title");
        if (title is not null && !title.Rules.Any(IsLayoutName)) title.Rules.Add(rule);
        return true;
    }

    /// <summary>
    /// Fields added to the built-in list after the user's sheet-fields.json was written (e.g. Status):
    /// appended so they show up. Returns true when the map changed.
    /// </summary>
    public static bool AddMissingFields(SheetFieldMap map)
    {
        bool changed = false;
        foreach (var field in Default().Fields)
        {
            if (map.Fields.Any(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase))) continue;
            map.Fields.Add(field);
            changed = true;
        }
        return changed;
    }

    /// <summary>Finds each field's property in a row. Returns field name → property (fields not found are absent).</summary>
    public Dictionary<string, PropertyValue> Match(TableRow row, IReadOnlyDictionary<string, ColumnKey> columns)
    {
        var found = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            foreach (var rule in field.Rules)
            {
                if (rule.Host is not null && !string.Equals(rule.Host, row.Source.Host.HostKind, StringComparison.OrdinalIgnoreCase)) continue;
                var names = rule.Names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
                PropertyValue? hit = null;
                foreach (var (columnId, value) in row.Values)
                {
                    if (!columns.TryGetValue(columnId, out var key) || key.Group == Group) continue;
                    if (rule.Group is not null && !key.Group.Contains(rule.Group, StringComparison.OrdinalIgnoreCase)) continue;
                    bool match = rule.Id is not null
                        ? string.Equals(value.Id, rule.Id, StringComparison.Ordinal)
                        : names.Contains(Normalize(value.Name));
                    if (!match) continue;
                    hit = value;
                    break;
                }
                if (hit is null) continue;
                found[field.Name] = hit;
                break;
            }
        }
        return found;
    }

    /// <summary>Upper case letters and digits only: "Sheet No." and "SHEET_NO" compare equal.</summary>
    public static string Normalize(string? s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static SheetFieldMap Default()
    {
        static FieldRule Revit(string id) => new() { Host = HostKinds.Revit, Id = id };
        static FieldRule RevitNamed(string group, params string[] names) => new() { Host = HostKinds.Revit, Group = group, Names = names.ToList() };
        static FieldRule Tag(params string[] tags) => new() { Host = HostKinds.AutoCAD, Group = "Title Block", Names = tags.ToList() };
        static FieldRule Acad(string group, params string[] names) => new() { Host = HostKinds.AutoCAD, Group = group, Names = names.ToList() };

        return new SheetFieldMap
        {
            Fields =
            {
                new SheetField { Name = NumberField, Rules =
                {
                    Revit("SHEET_NUMBER"),
                    Tag("DWGNO", "DWG_NO", "DWGNUM", "DRAWINGNO", "DRAWING_NO", "DRAWING_NUMBER", "DWG_NUMBER",
                        "SHEETNO", "SHEET_NO", "SHEETNUMBER", "SHEET_NUMBER", "SHTNO", "SHT_NO", "SHEET", "NUMBER", "DWG"),
                } },
                new SheetField { Name = "Title", Rules =
                {
                    Revit("SHEET_NAME"),
                    Tag("TITLE", "DWGTITLE", "DWG_TITLE", "DRAWINGTITLE", "DRAWING_TITLE", "SHEETTITLE", "SHEET_TITLE",
                        "SHEETNAME", "SHEET_NAME", "TITLE1", "TITLE_1", "TITLELINE1"),
                    // Most teams keep one sheet per drawing, so the layout name names the sheet.
                    Acad("Layout", "Layout Name"),
                } },
                new SheetField { Name = "Revision", Rules =
                {
                    RevitNamed("Current Revision", "Revision Number"),
                    Tag("REV", "REVISION", "REVNO", "REV_NO", "CURRENTREV", "CURRENT_REV", "REVNUM"),
                } },
                new SheetField { Name = "Revision Date", Rules =
                {
                    RevitNamed("Current Revision", "Revision Date"),
                    Tag("REVDATE", "REV_DATE", "REVISIONDATE"),
                } },
                new SheetField { Name = "Revision Description", Rules =
                {
                    RevitNamed("Current Revision", "Revision Description"),
                    Tag("REVDESC", "REV_DESC", "REVDESCRIPTION", "REVISIONDESCRIPTION"),
                } },
                new SheetField { Name = "Issue Date", Rules =
                {
                    Revit("SHEET_ISSUE_DATE"),
                    Tag("DATE", "ISSUEDATE", "ISSUE_DATE", "DWGDATE", "SHEETDATE", "PLOTDATE"),
                } },
                new SheetField { Name = "Drawn By", Rules =
                {
                    Revit("SHEET_DRAWN_BY"),
                    Tag("DRAWNBY", "DRAWN_BY", "DRAWN", "DRN", "DRNBY", "DWNBY", "DRAFTER", "DRAFTED"),
                } },
                new SheetField { Name = "Checked By", Rules =
                {
                    Revit("SHEET_CHECKED_BY"),
                    Tag("CHECKEDBY", "CHECKED_BY", "CHECKED", "CHK", "CHKBY", "CHKD"),
                } },
                new SheetField { Name = "Designed By", Rules =
                {
                    Revit("SHEET_DESIGNED_BY"),
                    Tag("DESIGNEDBY", "DESIGNED_BY", "DESIGNED", "DSGN", "DSN", "DES", "DESBY"),
                } },
                new SheetField { Name = "Approved By", Rules =
                {
                    Revit("SHEET_APPROVED_BY"),
                    Tag("APPROVEDBY", "APPROVED_BY", "APPROVED", "APP", "APPBY", "APPD"),
                } },
                new SheetField { Name = "Scale", Rules =
                {
                    Revit("SHEET_SCALE"),
                    Tag("SCALE", "DWGSCALE", "DWG_SCALE", "SHEETSCALE"),
                    Acad("Layout", "Viewport Scales"),
                } },
                new SheetField { Name = "Project Number", Rules =
                {
                    Tag("PROJNO", "PROJ_NO", "PROJECTNO", "PROJECT_NO", "PROJECTNUMBER", "JOBNO", "JOB_NO", "JOBNUMBER"),
                } },
                new SheetField { Name = "Status", Rules =
                {
                    // A sheet status parameter (project or shared), in any group of the sheet's properties.
                    new FieldRule { Host = HostKinds.Revit, Names = { "Sheet Status", "Status", "Drawing Status", "Issue Status" } },
                    Tag("STATUS", "SHEETSTATUS", "SHEET_STATUS", "DWGSTATUS", "DWG_STATUS", "ISSUESTATUS", "ISSUE_STATUS"),
                } },
            },
        };
    }
}
