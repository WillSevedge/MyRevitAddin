namespace Nexus.Hub.Core.Excel;

/// <summary>Which way a linked column goes.</summary>
public enum ExcelDirection
{
    /// <summary>Kept the same both ways (Compare offers either side).</summary>
    Both,
    /// <summary>Nexus fills the Excel column; Excel is never read back into the model.</summary>
    ToExcel,
    /// <summary>Excel feeds the model (Compare stages values as edits); Nexus never writes the column.</summary>
    ToModel,
    /// <summary>Read-only for Nexus: Excel calculates it (formulas) or it is not linked.</summary>
    None,
}

/// <summary>
/// Values an Excel column can take besides a Nexus column: the program (Revit, AutoCAD, Civil 3D, Plant 3D),
/// the file name, or a fixed text. Stored in <see cref="ExcelColumnMap.ColumnId"/> with a leading '='.
/// </summary>
public static class ExcelSources
{
    public const string Program = "=Program";
    public const string File = "=File";
    public const string Fixed = "=Fixed";

    public static bool IsSpecial(string? columnId) => columnId is { Length: > 0 } && columnId[0] == '=';

    /// <summary>"Revit", "AutoCAD", "Civil 3D", "Plant 3D" (the words in the Budget Tool's Source list).</summary>
    public static string ProgramOf(TableRow row) => row.Source.Host.Product is { Length: > 0 } p ? p : row.Source.Host.HostKind;

    /// <summary>The value Nexus has for this Excel column on this row; null when the row has no such value.</summary>
    public static string? ValueFor(ExcelColumnMap map, TableRow row) => map.ColumnId switch
    {
        Program => ProgramOf(row),
        File => row.Document,
        Fixed => map.FixedText ?? "",
        "" => null,
        _ => row.Values.TryGetValue(map.ColumnId, out var v) ? v.Value ?? "" : null,
    };

    /// <summary>Nexus writes this column into Excel.</summary>
    public static bool WritesExcel(ExcelColumnMap map) =>
        map.ColumnId.Length > 0 && !map.IsFormula && map.Direction is ExcelDirection.Both or ExcelDirection.ToExcel;

    /// <summary>Excel values in this column may be taken into the model.</summary>
    public static bool ReadsExcel(ExcelColumnMap map) =>
        map.ColumnId.Length > 0 && !IsSpecial(map.ColumnId) && map.Direction is ExcelDirection.Both or ExcelDirection.ToModel;
}
