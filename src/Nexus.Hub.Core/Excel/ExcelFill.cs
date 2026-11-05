namespace Nexus.Hub.Core.Excel;

/// <summary>The rows Nexus sends to Excel: every row with a key, by program then key (A-2 before A-10).</summary>
public static class ExcelFill
{
    /// <summary>Every row with a key, as (file, header → value), by program then key (A-2 before A-10).</summary>
    public static List<(string Document, IReadOnlyDictionary<string, string> Values)> Rows(ResultTable table, ExcelLink link)
    {
        var written = link.Columns.Where(ExcelSources.WritesExcel).ToList();
        return table.Rows
            .Where(r => r.Values.TryGetValue(link.KeyColumnId, out var k) && !string.IsNullOrWhiteSpace(k.Value))
            .Select(r => (r.Document, (IReadOnlyDictionary<string, string>)written
                .ToDictionary(c => c.Header, c => ExcelSources.ValueFor(c, r) ?? "", StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Program, then key, then file: the order rows are written in.</summary>
    public static List<IReadOnlyDictionary<string, string>> Ordered(IEnumerable<(string Document, IReadOnlyDictionary<string, string> Values)> rows, ExcelLink link)
    {
        string Value(IReadOnlyDictionary<string, string> v, string? header) => header is not null && v.TryGetValue(header, out var x) ? x : "";
        string? program = link.Columns.FirstOrDefault(c => c.ColumnId == ExcelSources.Program)?.Header;
        string? key = link.KeyHeader;
        return rows.OrderBy(r => Value(r.Values, program), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => Value(r.Values, key), NaturalComparer.Instance)
            .ThenBy(r => r.Document, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Values).ToList();
    }

    /// <summary>
    /// Match mode, Nexus → Excel: the cells of matched rows that differ in Nexus → Excel columns, and the rows
    /// only in the model (added below the list).
    /// </summary>
    public static (List<ExcelCellUpdate> Updates, List<IReadOnlyDictionary<string, string>> NewRows) MatchUpdates(
        ResultTable table, ExcelData excel, ExcelLink link)
    {
        var updates = new List<ExcelCellUpdate>();
        var newRows = new List<IReadOnlyDictionary<string, string>>();
        var byKey = new Dictionary<string, ExcelRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in excel.Rows) byKey.TryAdd(ExcelCompare.NormalizeKey(r.Key), r);
        var written = link.Columns.Where(c => ExcelSources.WritesExcel(c) && excel.HeaderColumns.ContainsKey(c.Header) && !excel.FormulaHeaders.Contains(c.Header)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            if (!row.Values.TryGetValue(link.KeyColumnId, out var k) || string.IsNullOrWhiteSpace(k.Value)) continue;
            string key = ExcelCompare.NormalizeKey(k.Value);
            if (!seen.Add(key)) continue;
            if (!byKey.TryGetValue(key, out var xr))
            {
                newRows.Add(written.ToDictionary(c => c.Header, c => ExcelSources.ValueFor(c, row) ?? "", StringComparer.OrdinalIgnoreCase));
                continue;
            }
            foreach (var c in written)
            {
                if (c.ColumnId == link.KeyColumnId) continue;
                string? want = ExcelSources.ValueFor(c, row);
                if (want is null || !xr.Cells.TryGetValue(c.Header, out var cell) || cell.HasFormula) continue;
                if (!ExcelCompare.SameValue(want, cell.Text)) updates.Add(new ExcelCellUpdate(cell.Row, cell.Column, want));
            }
        }
        return (updates, newRows);
    }
}

/// <summary>
/// Fill mode remembers the rows last sent for each file, so the rows of files that are not open now stay in the
/// workbook (open one model and the others' sheets are kept). Saved per workbook in %LOCALAPPDATA%\Nexus\excel-fill.
/// </summary>
public sealed class ExcelFillMemory
{
    public Dictionary<string, List<Dictionary<string, string>>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string PathFor(string workbook) =>
        System.IO.Path.Combine(Nexus.Contracts.NexusPaths.Root, "excel-fill",
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(workbook.ToUpperInvariant())))[..16] + ".json");

    public static ExcelFillMemory Load(string workbook)
    {
        try
        {
            string path = PathFor(workbook);
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<ExcelFillMemory>(File.ReadAllText(path), Nexus.Contracts.Json.Options) ?? new();
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the rows remembered for the workbook.", ex);
        }
        return new ExcelFillMemory();
    }

    public void Save(string workbook)
    {
        try
        {
            string path = PathFor(workbook);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this, Nexus.Contracts.Json.Options));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not remember the rows sent to the workbook.", ex);
        }
    }

    public static void Forget(string workbook)
    {
        try { File.Delete(PathFor(workbook)); } catch { /* nothing remembered */ }
    }

    /// <summary>
    /// The rows to write: the open files' rows now (remembered for next time), plus the remembered rows of
    /// files that are not open.
    /// </summary>
    public List<(string Document, IReadOnlyDictionary<string, string> Values)> Merge(
        IReadOnlyList<(string Document, IReadOnlyDictionary<string, string> Values)> current, IEnumerable<string> openFiles)
    {
        var open = new HashSet<string>(openFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in open) Files.Remove(file);
        foreach (var group in current.GroupBy(r => r.Document, StringComparer.OrdinalIgnoreCase))
            Files[group.Key] = group.Select(r => r.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)).ToList();
        return Files.SelectMany(f => f.Value.Select(v => (f.Key, (IReadOnlyDictionary<string, string>)v))).ToList();
    }

    /// <summary>Files remembered but not open now.</summary>
    public IEnumerable<string> Closed(IEnumerable<string> openFiles)
    {
        var open = new HashSet<string>(openFiles, StringComparer.OrdinalIgnoreCase);
        return Files.Keys.Where(f => !open.Contains(f));
    }
}
