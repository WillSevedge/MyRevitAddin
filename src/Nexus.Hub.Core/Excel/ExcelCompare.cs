using System.Globalization;

namespace Nexus.Hub.Core.Excel;

public enum ExcelDiffKind
{
    /// <summary>The row is in both; this column differs.</summary>
    Different,
    /// <summary>The key is in the model but not in the workbook.</summary>
    OnlyInModel,
    /// <summary>The key is in the workbook but not in the model.</summary>
    OnlyInExcel,
}

/// <summary>One difference between the workbook and the model.</summary>
public sealed class ExcelDiff
{
    public ExcelDiffKind Kind { get; init; }
    public string Key { get; init; } = "";
    /// <summary>Excel header (empty for whole-row differences).</summary>
    public string Header { get; init; } = "";
    public string ColumnId { get; init; } = "";
    public string? ModelValue { get; init; }
    public string? ExcelValue { get; init; }
    /// <summary>The model row (null when only in Excel).</summary>
    public TableRow? Row { get; init; }
    /// <summary>The Excel cell (null when only in the model).</summary>
    public ExcelCell? Cell { get; init; }
    /// <summary>Which way this column may go (from the link).</summary>
    public ExcelDirection Direction { get; init; } = ExcelDirection.Both;
}

public static class ExcelCompare
{
    /// <summary>
    /// Matches model rows to workbook rows on the key column and lists every linked value that
    /// differs, plus rows that are only on one side.
    /// </summary>
    public static List<ExcelDiff> Compare(ResultTable table, ExcelData excel, ExcelLink link, List<string> warnings)
    {
        var diffs = new List<ExcelDiff>();
        var model = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            if (!row.Values.TryGetValue(link.KeyColumnId, out var keyValue) || string.IsNullOrWhiteSpace(keyValue.Value)) continue;
            string key = NormalizeKey(keyValue.Value);
            if (!model.TryAdd(key, row))
                warnings.Add($"'{keyValue.Value}' appears more than once in the model ({model[key].Document}, {row.Document}); the first is compared.");
        }

        var excelRows = new Dictionary<string, ExcelRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in excel.Rows)
            if (!excelRows.TryAdd(NormalizeKey(r.Key), r))
                warnings.Add($"'{r.Key}' appears more than once in the workbook (rows {excelRows[NormalizeKey(r.Key)].Row} and {r.Row}); the first is compared.");

        // Program, file name and fixed text are Nexus → Excel only: not compared. Formula columns are Excel's.
        var linked = link.Columns.Where(c => c.ColumnId.Length > 0 && c.ColumnId != link.KeyColumnId
                                             && !ExcelSources.IsSpecial(c.ColumnId) && !c.IsFormula && c.Direction != ExcelDirection.None
                                             && !excel.FormulaHeaders.Contains(c.Header)).ToList();

        foreach (var (key, row) in model)
        {
            if (!excelRows.TryGetValue(key, out var xr))
            {
                diffs.Add(new ExcelDiff { Kind = ExcelDiffKind.OnlyInModel, Key = row.Values[link.KeyColumnId].Value ?? key, Row = row });
                continue;
            }
            foreach (var c in linked)
            {
                if (!xr.Cells.TryGetValue(c.Header, out var cell)) continue;
                row.Values.TryGetValue(c.ColumnId, out var mv);
                if (mv is null) continue; // this row has no such property (e.g. a Revit-only column on an AutoCAD row)
                if (SameValue(mv.Value, cell.Text)) continue;
                diffs.Add(new ExcelDiff
                {
                    Kind = ExcelDiffKind.Different, Key = xr.Key, Header = c.Header, ColumnId = c.ColumnId,
                    ModelValue = mv.Value ?? "", ExcelValue = cell.Text, Row = row, Cell = cell, Direction = c.Direction,
                });
            }
        }

        foreach (var (key, xr) in excelRows)
            if (!model.ContainsKey(key))
                diffs.Add(new ExcelDiff { Kind = ExcelDiffKind.OnlyInExcel, Key = xr.Key, Cell = xr.Cells.Values.FirstOrDefault() });

        return diffs
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Key, NaturalComparer.Instance)
            .ThenBy(d => link.Columns.FindIndex(c => c.Header == d.Header))
            .ToList();
    }

    /// <summary>
    /// Suggests a Nexus column for each header: sheet fields by name or by their tag synonyms,
    /// then any column with the same property name.
    /// </summary>
    public static List<ExcelColumnMap> AutoMap(IEnumerable<string> headers, ResultTable table, SheetFieldMap fields)
    {
        var bySynonym = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string columnId) => bySynonym.TryAdd(SheetFieldMap.Normalize(name), columnId);
        foreach (var f in fields.Fields)
        {
            string id = SheetFieldMap.ColumnId(f.Name);
            if (!table.ColumnsById.ContainsKey(id)) continue;
            Add(f.Name, id);
            Add("Sheet " + f.Name, id);
            Add("Drawing " + f.Name, id);
            foreach (var rule in f.Rules)
                foreach (var n in rule.Names) Add(n, id);
        }
        // Common header spellings.
        foreach (var (alias, field) in new[]
                 {
                     ("No", "Number"), ("Sheet No", "Number"), ("Sheet #", "Number"), ("Dwg No", "Number"), ("Drawing No", "Number"),
                     ("Sheet Name", "Title"), ("Sheet Title", "Title"), ("Drawing Title", "Title"), ("Name", "Title"), ("Description", "Title"),
                     ("Rev", "Revision"), ("Rev No", "Revision"), ("Current Revision", "Revision"), ("Date", "Issue Date"),
                     ("Rev Date", "Revision Date"), ("Revision Date", "Revision Date"), ("Rev Description", "Revision Description"),
                     ("Sheet Status", "Status"), ("Dwg Status", "Status"), ("Drawing Status", "Status"),
                     // The deliverables list's own headings, often repeated on its Model Export tab.
                     ("Dwg/Doc No", "Number"), ("Doc No", "Number"), ("Document No", "Number"), ("Drawing Number", "Number"),
                     ("Dwg Number", "Number"), ("Document Number", "Number"),
                     ("Drawing Title / Document Description", "Title"), ("Document Description", "Title"), ("Document Title", "Title"),
                 })
        {
            string id = SheetFieldMap.ColumnId(field);
            if (table.ColumnsById.ContainsKey(id)) Add(alias, id);
        }

        var byName = table.Columns
            .GroupBy(c => SheetFieldMap.Normalize(c.Name))
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        // Not model values: which program and which file a row comes from.
        var specials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "Source", "Program", "Software", "Application", "App", "Platform" }) specials[SheetFieldMap.Normalize(name)] = ExcelSources.Program;
        foreach (var name in new[] { "File", "File Name", "Filename", "Model", "Model File", "Drawing File", "Document" }) specials[SheetFieldMap.Normalize(name)] = ExcelSources.File;

        return headers.Select(h =>
        {
            string n = SheetFieldMap.Normalize(h);
            string id = bySynonym.TryGetValue(n, out var s) ? s : byName.TryGetValue(n, out var b) ? b : specials.TryGetValue(n, out var sp) ? sp : "";
            return new ExcelColumnMap
            {
                Header = h, ColumnId = id,
                // Program and file name can only be written; revisions and dates normally come from the model.
                Direction = ExcelSources.IsSpecial(id) ? ExcelDirection.ToExcel : ExcelDirection.Both,
            };
        }).ToList();
    }

    public static string NormalizeKey(string? key) => string.Join(' ', (key ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Equal as text (ignoring surrounding/duplicate spaces), as numbers, or as dates.</summary>
    public static bool SameValue(string? model, string? excel)
    {
        string a = NormalizeKey(model), b = NormalizeKey(excel);
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.CurrentCulture, out var x) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.CurrentCulture, out var y))
            return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
        if (a.Any(char.IsDigit) && DateTime.TryParse(a, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d1) &&
            DateTime.TryParse(b, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d2))
            return d1 == d2;
        return false;
    }
}

/// <summary>Sorts "A-2" before "A-10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var nx = x[si..i].TrimStart('0'); var ny = y[sj..j].TrimStart('0');
                int c = nx.Length != ny.Length ? nx.Length.CompareTo(ny.Length) : string.CompareOrdinal(nx, ny);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
