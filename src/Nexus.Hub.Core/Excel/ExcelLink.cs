using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core.Excel;

public enum ExcelLinkMode
{
    /// <summary>Rows are matched on the key column; missing rows are added below the list.</summary>
    Match,
    /// <summary>
    /// The table (or the rows under the headers) is filled with every row Nexus has, in order, and rows
    /// left over from before are cleared: a landing tab such as the Budget Tool's Model Export.
    /// </summary>
    Fill,
}

/// <summary>
/// How a workbook lines up with Nexus data: which worksheet (or Excel table), which row holds the headers,
/// which header is the key (normally the sheet number), which Excel column holds which Nexus value and in
/// which direction, and how live the link is. Saved per workbook in %LOCALAPPDATA%\Nexus\excel-links.json.
/// </summary>
public sealed class ExcelLink
{
    public string WorkbookPath { get; set; } = "";
    public string Worksheet { get; set; } = "";
    /// <summary>1-based row number of the header row.</summary>
    public int HeaderRow { get; set; } = 1;
    /// <summary>An Excel table (Insert › Table) the link works in, e.g. "ModelExport"; null for a plain range.</summary>
    public string? Table { get; set; }
    public ExcelLinkMode Mode { get; set; } = ExcelLinkMode.Match;
    /// <summary>Update the workbook after every read and every Review &amp; apply (Nexus → Excel columns).</summary>
    public bool AutoUpdate { get; set; }
    /// <summary>Tell the user when the workbook is saved with values that differ from the model.</summary>
    public bool Watch { get; set; } = true;
    /// <summary>Nexus column the rows are matched on.</summary>
    public string KeyColumnId { get; set; } = SheetFieldMap.NumberColumnId;
    public List<ExcelColumnMap> Columns { get; set; } = new();
    public DateTime SavedUtc { get; set; }

    public string? KeyHeader => Columns.FirstOrDefault(c => c.ColumnId == KeyColumnId)?.Header;
    public string Title => $"{Path.GetFileName(WorkbookPath)} › {(Table is { Length: > 0 } t ? t : Worksheet)}";

    private static string StoreFile => Path.Combine(NexusPaths.Root, "excel-links.json");

    /// <summary>Saved links, most recent first.</summary>
    public static List<ExcelLink> LoadAll()
    {
        try
        {
            if (File.Exists(StoreFile))
                return (JsonSerializer.Deserialize<List<ExcelLink>>(File.ReadAllText(StoreFile), Json.Options) ?? new())
                    .OrderByDescending(l => l.SavedUtc).ToList();
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the saved Excel links.", ex);
        }
        return new List<ExcelLink>();
    }

    public static ExcelLink? LoadFor(string workbookPath) =>
        LoadAll().FirstOrDefault(l => string.Equals(l.WorkbookPath, workbookPath, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        SavedUtc = DateTime.UtcNow;
        var all = LoadAll().Where(l => !string.Equals(l.WorkbookPath, WorkbookPath, StringComparison.OrdinalIgnoreCase)).ToList();
        all.Insert(0, this);
        Directory.CreateDirectory(NexusPaths.Root);
        File.WriteAllText(StoreFile, JsonSerializer.Serialize(all.Take(50).ToList(), Json.Options));
    }
}

/// <summary>One Excel column ↔ one Nexus value.</summary>
public sealed class ExcelColumnMap
{
    public string Header { get; set; } = "";
    /// <summary>
    /// A Nexus column id ("Sheet › Title", "Title Block › DWGNO", ...), a special value (<see cref="ExcelSources"/>:
    /// program, file name, fixed text), or empty when not linked.
    /// </summary>
    public string ColumnId { get; set; } = "";
    public ExcelDirection Direction { get; set; } = ExcelDirection.Both;
    /// <summary>The text for <see cref="ExcelSources.Fixed"/>.</summary>
    public string? FixedText { get; set; }
    /// <summary>Excel calculates this column (a formula): Nexus never writes it.</summary>
    public bool IsFormula { get; set; }
}
