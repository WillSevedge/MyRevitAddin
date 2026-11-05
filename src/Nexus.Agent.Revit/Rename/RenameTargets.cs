using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Nexus.Rename;

namespace Nexus.Agent.Revit.Rename;

/// <summary>
/// One kind of thing Bulk Rename can rename in a Revit model: how to find them, read and set the name,
/// and which names must be unique together (Revit refuses a name already used in that scope).
/// </summary>
internal sealed class RenameTarget
{
    public required string Id { get; init; }
    /// <summary>Group in the list on the left ("Views & Sheets", "Families & Types"...).</summary>
    public required string Group { get; init; }
    public required string Label { get; init; }
    public string Tip { get; init; } = "";
    public required Func<Document, IEnumerable<object>> Collect { get; init; }
    public required Func<object, string> Get { get; init; }
    public required Action<Document, object, string> Set { get; init; }
    /// <summary>Uniqueness scope; null when Revit allows the same name twice.</summary>
    public Func<object, string?> Scope { get; init; } = _ => null;
    public Func<Document, object, string> Detail { get; init; } = (_, _) => "";
    public string Forbidden { get; init; } = RenameTargets.ForbiddenInNames;
}

internal static class RenameTargets
{
    /// <summary>Characters Revit does not allow in names of views, sheets, levels, families, types...</summary>
    public const string ForbiddenInNames = "\\:{}[]|;<>?`~";

    public static readonly IReadOnlyList<RenameTarget> All = Create();

    private static IEnumerable<T> OfClass<T>(Document doc) where T : Element =>
        new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();

    private static string NameOf(object o) => o is Element e ? e.Name : "";
    private static void SetName(Document doc, object o, string name) => ((Element)o).Name = name;

    private static string Param(object o, BuiltInParameter bip) => (o as Element)?.get_Parameter(bip)?.AsString() ?? "";

    private static void SetParam(object o, BuiltInParameter bip, string value)
    {
        var p = ((Element)o).get_Parameter(bip) ?? throw new InvalidOperationException("This element has no such parameter.");
        if (p.IsReadOnly) throw new InvalidOperationException("The value is read-only.");
        p.Set(value);
    }

    private static readonly Regex Words = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)", RegexOptions.Compiled);

    /// <summary>"FloorPlan" → "Floor Plan".</summary>
    public static string Spaced(string text) => Words.Replace(text, " ");

    private static bool IsRenameableView(View v) =>
        v.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined
            or ViewType.DrawingSheet or ViewType.Schedule or ViewType.ColumnSchedule or ViewType.PanelSchedule);

    private static string LevelName(Document doc, object o) => o switch
    {
        View v => v.GenLevel?.Name ?? "",
        SpatialElement s => s.Level?.Name ?? "",
        Element e when e.LevelId != ElementId.InvalidElementId => doc.GetElement(e.LevelId)?.Name ?? "",
        _ => "",
    };

    private static List<RenameTarget> Create() => new()
    {
        // ------------------------------------------------------------------ views and sheets
        new()
        {
            Id = "views", Group = "Views & Sheets", Label = "Views",
            Tip = "Plans, ceiling plans, sections, elevations, 3D, drafting, legends, callouts... (names are unique per view type)",
            Collect = doc => OfClass<View>(doc).Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && IsRenameableView(v)),
            Get = NameOf, Set = SetName,
            Scope = o => "view:" + ((View)o).ViewType,
            Detail = (doc, o) => Spaced(((View)o).ViewType.ToString()) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
        },
        new()
        {
            Id = "sheet-number", Group = "Views & Sheets", Label = "Sheet Numbers",
            Collect = doc => OfClass<ViewSheet>(doc), Get = o => ((ViewSheet)o).SheetNumber,
            Set = (_, o, v) => ((ViewSheet)o).SheetNumber = v,
            Scope = _ => "sheet-number",
            Detail = (_, o) => ((ViewSheet)o).Name,
        },
        new()
        {
            Id = "sheet-name", Group = "Views & Sheets", Label = "Sheet Names",
            Collect = doc => OfClass<ViewSheet>(doc), Get = NameOf, Set = SetName,
            Detail = (_, o) => ((ViewSheet)o).SheetNumber,
        },
        new()
        {
            Id = "schedules", Group = "Views & Sheets", Label = "Schedules",
            Collect = doc => OfClass<ViewSchedule>(doc).Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule),
            Get = NameOf, Set = SetName,
            Scope = _ => "schedule",
            Detail = (doc, o) => ((ViewSchedule)o).Definition?.CategoryId is { } c && c != ElementId.InvalidElementId
                ? Category.GetCategory(doc, c)?.Name ?? "" : "",
        },
        new()
        {
            Id = "templates", Group = "Views & Sheets", Label = "View Templates",
            Collect = doc => OfClass<View>(doc).Where(v => v.IsTemplate),
            Get = NameOf, Set = SetName,
            Scope = o => "template:" + ((View)o).ViewType,
            Detail = (_, o) => Spaced(((View)o).ViewType.ToString()),
        },

        // ------------------------------------------------------------------ datums
        new()
        {
            Id = "levels", Group = "Levels & Grids", Label = "Levels",
            Collect = doc => OfClass<Level>(doc).OrderBy(l => l.Elevation), Get = NameOf, Set = SetName,
            Scope = _ => "level",
            Detail = (doc, o) => UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, ((Level)o).Elevation, false),
        },
        new()
        {
            Id = "grids", Group = "Levels & Grids", Label = "Grids",
            Collect = doc => OfClass<Grid>(doc), Get = NameOf, Set = SetName,
            Scope = _ => "grid",
        },
        new()
        {
            Id = "scope-boxes", Group = "Levels & Grids", Label = "Scope Boxes",
            Collect = doc => new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().Cast<object>(),
            Get = NameOf, Set = SetName,
            Scope = _ => "scope-box",
        },
        new()
        {
            Id = "ref-planes", Group = "Levels & Grids", Label = "Reference Planes (named)",
            Collect = doc => OfClass<ReferencePlane>(doc).Where(r => r.Name.Length > 0 && r.Name != "Reference Plane"),
            Get = NameOf, Set = SetName,
        },

        // ------------------------------------------------------------------ rooms, spaces, areas
        new()
        {
            Id = "room-name", Group = "Rooms, Spaces & Areas", Label = "Room Names",
            Collect = doc => OfClass<SpatialElement>(doc).OfType<Room>(),
            Get = o => Param(o, BuiltInParameter.ROOM_NAME), Set = (_, o, v) => SetParam(o, BuiltInParameter.ROOM_NAME, v),
            Detail = (doc, o) => Param(o, BuiltInParameter.ROOM_NUMBER) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
            Forbidden = "",
        },
        new()
        {
            Id = "room-number", Group = "Rooms, Spaces & Areas", Label = "Room Numbers",
            Collect = doc => OfClass<SpatialElement>(doc).OfType<Room>(),
            Get = o => Param(o, BuiltInParameter.ROOM_NUMBER), Set = (_, o, v) => SetParam(o, BuiltInParameter.ROOM_NUMBER, v),
            Detail = (doc, o) => Param(o, BuiltInParameter.ROOM_NAME) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
            Forbidden = "",
        },
        new()
        {
            Id = "space-name", Group = "Rooms, Spaces & Areas", Label = "Space Names",
            Collect = doc => OfClass<SpatialElement>(doc).OfType<Space>(),
            Get = o => Param(o, BuiltInParameter.ROOM_NAME), Set = (_, o, v) => SetParam(o, BuiltInParameter.ROOM_NAME, v),
            Detail = (doc, o) => Param(o, BuiltInParameter.ROOM_NUMBER) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
            Forbidden = "",
        },
        new()
        {
            Id = "space-number", Group = "Rooms, Spaces & Areas", Label = "Space Numbers",
            Collect = doc => OfClass<SpatialElement>(doc).OfType<Space>(),
            Get = o => Param(o, BuiltInParameter.ROOM_NUMBER), Set = (_, o, v) => SetParam(o, BuiltInParameter.ROOM_NUMBER, v),
            Detail = (doc, o) => Param(o, BuiltInParameter.ROOM_NAME) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
            Forbidden = "",
        },
        new()
        {
            Id = "area-name", Group = "Rooms, Spaces & Areas", Label = "Area Names",
            Collect = doc => OfClass<SpatialElement>(doc).OfType<Area>(),
            Get = o => Param(o, BuiltInParameter.ROOM_NAME), Set = (_, o, v) => SetParam(o, BuiltInParameter.ROOM_NAME, v),
            Detail = (doc, o) => Param(o, BuiltInParameter.ROOM_NUMBER) + (LevelName(doc, o) is { Length: > 0 } l ? " · " + l : ""),
            Forbidden = "",
        },

        // ------------------------------------------------------------------ families and types
        new()
        {
            Id = "families", Group = "Families & Types", Label = "Families",
            Collect = doc => OfClass<Family>(doc).Where(f => !f.IsInPlace && f.IsEditable),
            Get = NameOf, Set = SetName,
            Scope = _ => "family",
            Detail = (_, o) => ((Family)o).FamilyCategory?.Name ?? "",
        },
        new()
        {
            Id = "family-types", Group = "Families & Types", Label = "Family Types",
            Collect = doc => OfClass<FamilySymbol>(doc).Where(s => s.Family is { IsInPlace: false }),
            Get = NameOf, Set = SetName,
            Scope = o => "type:" + ((FamilySymbol)o).Family.Id.Value.ToString(CultureInfo.InvariantCulture),
            Detail = (_, o) => $"{((FamilySymbol)o).Category?.Name} · {((FamilySymbol)o).FamilyName}",
        },
        new()
        {
            Id = "system-types", Group = "Families & Types", Label = "System Types",
            Tip = "Wall, floor, roof, ceiling, pipe, duct, text, dimension, view family types... (unique within the same system family)",
            Collect = doc => new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => t is not FamilySymbol && t is not GroupType && t.Category is not null && t.CanBeRenamed),
            Get = NameOf, Set = SetName,
            Scope = o => "systype:" + ((ElementType)o).Category!.Id.Value.ToString(CultureInfo.InvariantCulture) + ":" + ((ElementType)o).FamilyName,
            Detail = (_, o) => $"{((ElementType)o).Category?.Name} · {((ElementType)o).FamilyName}",
        },
        new()
        {
            Id = "group-types", Group = "Families & Types", Label = "Group Types",
            Collect = doc => OfClass<GroupType>(doc), Get = NameOf, Set = SetName,
            Scope = o => "group:" + (((GroupType)o).Category?.Id.Value.ToString(CultureInfo.InvariantCulture) ?? ""),
            Detail = (_, o) => ((GroupType)o).Category?.Name ?? "",
        },

        // ------------------------------------------------------------------ model settings
        new()
        {
            Id = "materials", Group = "Settings", Label = "Materials",
            Collect = doc => OfClass<Material>(doc), Get = NameOf, Set = SetName,
            Scope = _ => "material",
            Detail = (_, o) => ((Material)o).MaterialClass ?? "",
        },
        new()
        {
            Id = "filters", Group = "Settings", Label = "View Filters",
            Collect = doc => OfClass<ParameterFilterElement>(doc), Get = NameOf, Set = SetName,
            Scope = _ => "filter",
        },
        new()
        {
            Id = "line-patterns", Group = "Settings", Label = "Line Patterns",
            Collect = doc => OfClass<LinePatternElement>(doc), Get = NameOf, Set = SetName,
            Scope = _ => "line-pattern",
        },
        new()
        {
            Id = "fill-patterns", Group = "Settings", Label = "Fill Patterns",
            Collect = doc => OfClass<FillPatternElement>(doc), Get = NameOf, Set = SetName,
            Scope = o => "fill-pattern:" + ((FillPatternElement)o).GetFillPattern().Target,
            Detail = (_, o) => ((FillPatternElement)o).GetFillPattern().Target.ToString(),
        },
        new()
        {
            Id = "phases", Group = "Settings", Label = "Phases",
            Collect = doc => doc.Phases.Cast<Phase>(), Get = NameOf, Set = SetName,
            Scope = _ => "phase",
        },
        new()
        {
            Id = "worksets", Group = "Settings", Label = "Worksets",
            Tip = "User worksets of a workshared model.",
            Collect = doc => doc.IsWorkshared
                ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Cast<object>()
                : Enumerable.Empty<object>(),
            Get = o => ((Workset)o).Name,
            Set = (doc, o, v) => WorksetTable.RenameWorkset(doc, ((Workset)o).Id, v),
            Scope = _ => "workset",
            Detail = (_, o) => ((Workset)o).Owner is { Length: > 0 } owner ? "Owned by " + owner : "",
        },
    };
}
