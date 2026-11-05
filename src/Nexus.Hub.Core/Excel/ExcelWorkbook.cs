using System.Globalization;
using ClosedXML.Excel;

namespace Nexus.Hub.Core.Excel;

/// <summary>A worksheet's header row and headers, for setting up a link.</summary>
public sealed class ExcelSheetInfo
{
    public string Name { get; init; } = "";
    public int HeaderRow { get; init; }
    public List<string> Headers { get; init; } = new();
    /// <summary>Excel tables (Insert › Table) on this worksheet.</summary>
    public List<ExcelTableInfo> Tables { get; init; } = new();
}

/// <summary>An Excel table: where it is, its columns, and which columns Excel calculates.</summary>
public sealed class ExcelTableInfo
{
    public string Name { get; init; } = "";
    public string Worksheet { get; init; } = "";
    public int HeaderRow { get; init; }
    public int FirstDataRow { get; init; }
    public int LastRow { get; init; }
    public List<string> Headers { get; init; } = new();
    /// <summary>Columns with formulas (calculated columns): never written.</summary>
    public HashSet<string> FormulaHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int DataRows => Math.Max(0, LastRow - FirstDataRow + 1);
    public override string ToString() => $"{Name} ({DataRows} rows)";
}

public sealed class ExcelCell
{
    public int Row { get; init; }
    public int Column { get; init; }
    /// <summary>The text Excel shows (number formats applied).</summary>
    public string Text { get; init; } = "";
    public bool HasFormula { get; init; }
}

public sealed class ExcelRow
{
    public int Row { get; init; }
    public string Key { get; init; } = "";
    /// <summary>Header → cell, for the linked columns.</summary>
    public Dictionary<string, ExcelCell> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExcelData
{
    /// <summary>Rows that have a key (compared with the model).</summary>
    public List<ExcelRow> Rows { get; } = new();
    /// <summary>Every row of the table or list, empty ones included (for filling).</summary>
    public Dictionary<int, ExcelRow> Area { get; } = new();
    /// <summary>The worksheet the link reads (the table's worksheet for a table link).</summary>
    public string Worksheet { get; set; } = "";
    public int FirstDataRow { get; set; }
    /// <summary>Last row of the Excel table; 0 when the link is not to a table.</summary>
    public int TableLastRow { get; set; }
    /// <summary>Columns with formulas: never written.</summary>
    public HashSet<string> FormulaHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Header → 1-based column number.</summary>
    public Dictionary<string, int> HeaderColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last row that belongs to the table (new rows go below it).</summary>
    public int LastRow { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Reads and writes linked workbooks (.xlsx, .xlsm). Reading works while the file is open in Excel.</summary>
public static class ExcelWorkbook
{
    /// <summary>Stop reading data after this many empty rows in a row.</summary>
    private const int MaxBlankRows = 20;

    public static List<ExcelSheetInfo> Inspect(string path)
    {
        using var wb = OpenForRead(path);
        var sheets = new List<ExcelSheetInfo>();
        foreach (var ws in wb.Worksheets)
        {
            var tables = ws.Tables.Select(t => TableInfo(ws, t)).ToList();
            // A table's header row is the natural header row of its worksheet.
            int headerRow = tables.FirstOrDefault()?.HeaderRow ?? GuessHeaderRow(ws);
            sheets.Add(new ExcelSheetInfo
            {
                Name = ws.Name,
                HeaderRow = headerRow,
                Headers = headerRow == 0 ? new() : Headers(ws, headerRow).Select(h => h.Header).ToList(),
                Tables = tables,
            });
        }
        return sheets;
    }

    public static List<string> HeadersAt(string path, string worksheet, int headerRow)
    {
        using var wb = OpenForRead(path);
        return Headers(wb.Worksheet(worksheet), headerRow).Select(h => h.Header).ToList();
    }

    internal static ExcelTableInfo TableInfoOf(IXLWorksheet ws, IXLTable t) => TableInfo(ws, t);

    private static ExcelTableInfo TableInfo(IXLWorksheet ws, IXLTable t)
    {
        var range = t.RangeAddress;
        int headerRow = range.FirstAddress.RowNumber;
        int first = t.ShowHeaderRow ? headerRow + 1 : headerRow;
        int last = range.LastAddress.RowNumber - (t.ShowTotalsRow ? 1 : 0);
        var info = new ExcelTableInfo
        {
            Name = t.Name, Worksheet = ws.Name, HeaderRow = headerRow, FirstDataRow = first, LastRow = last,
            Headers = t.Fields.Select(f => f.Name).ToList(),
        };
        // A calculated column: formulas in its data rows (the first rows are enough; one may be blank).
        foreach (var f in t.Fields)
        {
            int column = range.FirstAddress.ColumnNumber + f.Index;
            for (int r = first; r <= Math.Min(last, first + 20); r++)
                if (ws.Cell(r, column).HasFormula) { info.FormulaHeaders.Add(f.Name); break; }
        }
        return info;
    }

    /// <summary>The table with this name, on any worksheet.</summary>
    private static (IXLWorksheet Sheet, IXLTable Table)? FindTable(XLWorkbook wb, string name)
    {
        foreach (var ws in wb.Worksheets)
            foreach (var t in ws.Tables)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return (ws, t);
        return null;
    }

    public static ExcelData Read(ExcelLink link)
    {
        using var wb = OpenForRead(link.WorkbookPath);
        if (link.Table is { Length: > 0 } tableName)
            return ReadTable(wb, link, tableName);
        if (!wb.TryGetWorksheet(link.Worksheet, out var ws))
            throw new InvalidOperationException($"The workbook has no worksheet '{link.Worksheet}'.");

        var data = new ExcelData { Worksheet = ws.Name, FirstDataRow = link.HeaderRow + 1 };
        foreach (var (header, column) in Headers(ws, link.HeaderRow))
            data.HeaderColumns.TryAdd(header, column);

        var linked = link.Columns.Where(c => c.ColumnId.Length > 0 && data.HeaderColumns.ContainsKey(c.Header)).ToList();
        foreach (var missing in link.Columns.Where(c => c.ColumnId.Length > 0 && !data.HeaderColumns.ContainsKey(c.Header)))
            data.Warnings.Add($"Column '{missing.Header}' is no longer in row {link.HeaderRow} of '{link.Worksheet}'.");

        string? keyHeader = link.KeyHeader;
        if (keyHeader is null || !data.HeaderColumns.TryGetValue(keyHeader, out int keyColumn))
            throw new InvalidOperationException("The link has no key column (e.g. the sheet number). Set it up again.");

        int last = ws.LastRowUsed()?.RowNumber() ?? link.HeaderRow;
        int blanks = 0;
        data.LastRow = link.HeaderRow;
        for (int r = link.HeaderRow + 1; r <= last; r++)
        {
            string key = Text(ws.Cell(r, keyColumn)).Trim();
            bool empty = key.Length == 0 && linked.All(c => Text(ws.Cell(r, data.HeaderColumns[c.Header])).Trim().Length == 0);
            if (empty)
            {
                if (++blanks >= MaxBlankRows) break;
                continue;
            }
            blanks = 0;
            data.LastRow = r;
            var row = RowAt(ws, r, key, linked, data);
            data.Area[r] = row;
            if (key.Length == 0) continue; // a row without a key (subtotal, note...) is not compared
            data.Rows.Add(row);
        }
        return data;
    }

    private static ExcelRow RowAt(IXLWorksheet ws, int r, string key, IEnumerable<ExcelColumnMap> linked, ExcelData data)
    {
        var row = new ExcelRow { Row = r, Key = key };
        foreach (var c in linked)
        {
            int col = data.HeaderColumns[c.Header];
            var cell = ws.Cell(r, col);
            row.Cells[c.Header] = new ExcelCell { Row = r, Column = col, Text = Text(cell), HasFormula = cell.HasFormula };
            if (cell.HasFormula) data.FormulaHeaders.Add(c.Header);
        }
        return row;
    }

    /// <summary>A link to an Excel table: its own header row and every data row (rows without a key are kept in Area).</summary>
    private static ExcelData ReadTable(XLWorkbook wb, ExcelLink link, string tableName)
    {
        var found = FindTable(wb, tableName)
                    ?? throw new InvalidOperationException($"The workbook has no table '{tableName}' any more. Link it again (Excel › Link workbook).");
        var (ws, table) = found;
        var info = TableInfo(ws, table);
        var data = new ExcelData { Worksheet = ws.Name, FirstDataRow = info.FirstDataRow, TableLastRow = info.LastRow, LastRow = info.HeaderRow };
        int firstColumn = table.RangeAddress.FirstAddress.ColumnNumber;
        foreach (var f in table.Fields) data.HeaderColumns.TryAdd(f.Name, firstColumn + f.Index);
        foreach (var h in info.FormulaHeaders) data.FormulaHeaders.Add(h);

        var linked = link.Columns.Where(c => c.ColumnId.Length > 0 && data.HeaderColumns.ContainsKey(c.Header)).ToList();
        foreach (var missing in link.Columns.Where(c => c.ColumnId.Length > 0 && !data.HeaderColumns.ContainsKey(c.Header)))
            data.Warnings.Add($"Column '{missing.Header}' is no longer in table '{tableName}'.");
        string? keyHeader = link.KeyHeader;
        if (keyHeader is null || !data.HeaderColumns.TryGetValue(keyHeader, out int keyColumn))
            throw new InvalidOperationException("The link has no key column (e.g. the sheet number). Set it up again.");

        for (int r = info.FirstDataRow; r <= info.LastRow; r++)
        {
            string key = Text(ws.Cell(r, keyColumn)).Trim();
            var row = RowAt(ws, r, key, linked, data);
            data.Area[r] = row;
            if (row.Cells.Values.Any(c => !c.HasFormula && c.Text.Trim().Length > 0)) data.LastRow = r;
            if (key.Length > 0) data.Rows.Add(row);
        }
        return data;
    }

    /// <summary>
    /// Fill mode: what to write so the table (or the rows under the headers) holds exactly <paramref name="rows"/>
    /// (header → value, in order): changed cells only, rows left over from before cleared, and the table made
    /// longer when there are more rows than it has. Formula columns are never touched.
    /// </summary>
    public static ExcelFillPlan PlanFill(ExcelLink link, ExcelData data, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var plan = new ExcelFillPlan { Worksheet = data.Worksheet, Table = link.Table, Rows = rows.Count };
        var written = link.Columns.Where(c => ExcelSources.WritesExcel(c) && data.HeaderColumns.ContainsKey(c.Header) && !data.FormulaHeaders.Contains(c.Header))
            .Select(c => (c.Header, Column: data.HeaderColumns[c.Header])).ToList();
        int start = data.FirstDataRow;
        string Current(int r, string header) => data.Area.TryGetValue(r, out var row) && row.Cells.TryGetValue(header, out var cell) ? cell.Text : "";

        for (int i = 0; i < rows.Count; i++)
        {
            int r = start + i;
            bool changed = false;
            foreach (var (header, column) in written)
            {
                string want = rows[i].TryGetValue(header, out var v) ? v ?? "" : "";
                string now = Current(r, header);
                if (ExcelCompare.NormalizeKey(want) == ExcelCompare.NormalizeKey(now) || (want.Length > 0 && ExcelCompare.SameValue(want, now))) continue;
                plan.Updates.Add(new ExcelCellUpdate(r, column, want));
                changed = true;
            }
            if (changed) plan.ChangedRows++;
        }
        int lastUsed = data.TableLastRow > 0 ? data.TableLastRow : data.LastRow;
        for (int r = start + rows.Count; r <= lastUsed; r++)
        {
            bool cleared = false;
            foreach (var (header, column) in written)
            {
                if (Current(r, header).Trim().Length == 0) continue;
                plan.Updates.Add(new ExcelCellUpdate(r, column, ""));
                cleared = true;
            }
            if (cleared) plan.ClearedRows++;
        }
        if (data.TableLastRow > 0 && start + rows.Count - 1 > data.TableLastRow)
            plan.GrowToRow = start + rows.Count - 1;
        return plan;
    }

    /// <summary>
    /// Match mode as a plan: <paramref name="updates"/> plus <paramref name="newRows"/> placed in the first empty rows
    /// of the table (or below the list), with the table made longer when it is full.
    /// </summary>
    public static ExcelFillPlan PlanMatch(ExcelLink link, ExcelData data, IReadOnlyList<ExcelCellUpdate> updates,
        IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var plan = new ExcelFillPlan { Worksheet = data.Worksheet, Table = link.Table, Rows = data.Rows.Count + newRows.Count };
        plan.Updates.AddRange(updates);
        plan.ChangedRows = updates.Select(u => u.Row).Distinct().Count();
        if (newRows.Count == 0) return plan;

        var written = link.Columns.Where(c => ExcelSources.WritesExcel(c) && data.HeaderColumns.ContainsKey(c.Header) && !data.FormulaHeaders.Contains(c.Header))
            .Select(c => (c.Header, Column: data.HeaderColumns[c.Header])).ToList();
        bool Empty(int r) => !data.Area.TryGetValue(r, out var row) || row.Cells.Values.All(c => c.HasFormula || c.Text.Trim().Length == 0);
        int r = data.FirstDataRow;
        int tableEnd = data.TableLastRow > 0 ? data.TableLastRow : int.MaxValue;
        foreach (var values in newRows)
        {
            // Empty rows inside the table first; then below the list (the table grows to take them).
            while (r <= Math.Min(tableEnd, data.LastRow) && !Empty(r)) r++;
            foreach (var (header, column) in written)
                if (values.TryGetValue(header, out var v) && v.Length > 0) plan.Updates.Add(new ExcelCellUpdate(r, column, v));
            plan.ChangedRows++;
            r++;
        }
        if (data.TableLastRow > 0 && r - 1 > data.TableLastRow) plan.GrowToRow = r - 1;
        return plan;
    }

    /// <summary>Writes a fill plan (after a backup, with the same checks as <see cref="Write"/>). Returns the backup path.</summary>
    public static string WriteFill(ExcelLink link, ExcelFillPlan plan)
    {
        if (plan.IsEmpty) return "";
        return WriteVerified(link.WorkbookPath, plan.Worksheet, plan.Updates.Select(u => (u.Row, u.Column, u.Value)).ToList(),
            plan.GrowToRow > 0 && plan.Table is { } t ? (t, plan.GrowToRow) : null);
    }

    /// <summary>
    /// Writes values into the workbook, after copying it to the backups folder, and returns the backup path.
    /// Only the given cells change: the edit is made cell by cell on a copy, the copy is compared with the
    /// original (macros, form controls, comments, validation, conditional formatting, tables, custom XML,
    /// every other cell), and the original is replaced only if nothing else changed. New rows go into the
    /// empty rows below the last row of the list; rows are never inserted, so formulas and pre-built rows
    /// further down stay where they are. Fails with a clear message if the file is open in Excel.
    /// </summary>
    public static string Write(ExcelLink link, IReadOnlyList<ExcelCellUpdate> updates, IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var values = updates.Select(u => (u.Row, u.Column, u.Value)).ToList();
        if (newRows.Count > 0) values.AddRange(PlaceNewRows(link, newRows));
        if (values.Count == 0) return "";
        return WriteVerified(link.WorkbookPath, link.Worksheet, values, null);
    }

    /// <summary>The cells for new rows: the first empty rows below the list (no formula, no value in the written columns).</summary>
    private static List<(int Row, int Column, string Value)> PlaceNewRows(ExcelLink link, IReadOnlyList<IReadOnlyDictionary<string, string>> newRows)
    {
        var data = Read(link);
        using var wb = OpenForRead(link.WorkbookPath);
        var ws = wb.Worksheet(link.Worksheet);
        var cells = new List<(int, int, string)>();
        int row = data.LastRow;
        foreach (var values in newRows)
        {
            var columns = values.Where(v => data.HeaderColumns.ContainsKey(v.Key)).Select(v => (Column: data.HeaderColumns[v.Key], v.Value)).ToList();
            int limit = row + 200;
            do
            {
                if (++row > limit)
                    throw new InvalidOperationException($"No empty row was found below row {data.LastRow} of '{link.Worksheet}' for the new rows.");
            }
            while (columns.Any(c => ws.Cell(row, c.Column).HasFormula || !ws.Cell(row, c.Column).IsEmpty()));
            foreach (var (column, value) in columns) cells.Add((row, column, value));
        }
        return cells;
    }

    private static string WriteVerified(string path, string worksheet, List<(int Row, int Column, string Value)> values,
        (string Table, int LastRow)? grow)
    {
        string name = Path.GetFileName(path);
        string closeIt = $"'{name}' is open in Excel (or another program). Save and close it there, then try again.";
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        catch (IOException ex)
        {
            throw new IOException(closeIt, ex);
        }

        string backup = Backup(path);
        string work = Path.Combine(Path.GetDirectoryName(backup)!, $"~writing {Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.Copy(backup, work);
        try
        {
            var before = WorkbookSnapshot.Take(backup);
            ExcelTableGrowth? growth = grow is { } g ? ExcelCellWriter.GrowTable(work, worksheet, g.Table, g.LastRow) : null;
            var stored = ExcelCellWriter.Write(work, worksheet, values);
            var after = WorkbookSnapshot.Take(work);

            var mayChange = PartsAWriteMayChange(before, worksheet);
            if (growth is not null) mayChange.Add(growth.TablePart);
            // A first text value creates the shared string table, which adds a workbook relationship.
            if (before.SharedStringsPart is null && before.WorkbookPart is { } wbPart)
                mayChange.Add(wbPart[..(wbPart.LastIndexOf('/') + 1)] + "_rels/" + wbPart[(wbPart.LastIndexOf('/') + 1)..] + ".rels");
            var issues = WorkbookSnapshot.Compare(before, after,
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [worksheet] = stored },
                mayChange,
                growth is null ? null : new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase) { [worksheet] = growth.FormulaCells });
            if (issues.Count > 0)
            {
                foreach (var issue in issues) HubLog.Warn($"Excel write check failed for {name}: {issue}");
                throw new ExcelWriteVerificationException(name, issues);
            }

            try
            {
                File.Copy(work, path, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new IOException(closeIt, ex);
            }
            return backup;
        }
        finally
        {
            try { File.Delete(work); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parts a cell-level write to <paramref name="worksheet"/> is allowed to change: that worksheet,
    /// the shared strings and the workbook part (to ask Excel to recalculate on open). Every other part
    /// must come out byte-identical.
    /// </summary>
    public static HashSet<string> PartsAWriteMayChange(WorkbookSnapshot snapshot, string worksheet)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Sheets.TryGetValue(worksheet, out var sheet)) parts.Add(sheet.PartName);
        if (snapshot.SharedStringsPart is { } sst) parts.Add(sst);
        else parts.Add("/xl/sharedStrings.xml");
        if (snapshot.WorkbookPart is { } wb) parts.Add(wb);
        return parts;
    }

    /// <summary>A new workbook with one header row and the given rows (Export to Excel).</summary>
    public static void Export(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows, string sheetName = "Nexus")
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheetName);
        for (int c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
        int r = 2;
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
                if (!string.IsNullOrEmpty(row[c])) ws.Cell(r, c + 1).Value = row[c];
            r++;
        }
        var header = ws.Range(1, 1, 1, Math.Max(1, headers.Count));
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDE6F2");
        ws.SheetView.FreezeRows(1);
        if (r > 2) ws.Range(1, 1, r - 1, Math.Max(1, headers.Count)).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(r, 200), 8, 60);
        wb.SaveAs(path);
    }

    private static string Backup(string path)
    {
        string dir = Path.Combine(Nexus.Contracts.NexusPaths.Root, "excel-backups");
        Directory.CreateDirectory(dir);
        string backup = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyy-MM-dd HHmmss}{Path.GetExtension(path)}");
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var dst = File.Create(backup))
            src.CopyTo(dst);
        return backup;
    }

    private static XLWorkbook OpenForRead(string path)
    {
        // FileShare.ReadWrite: read even while Excel has the workbook open.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var memory = new MemoryStream();
        using (stream) stream.CopyTo(memory);
        memory.Position = 0;
        return new XLWorkbook(memory);
    }

    private static IEnumerable<(string Header, int Column)> Headers(IXLWorksheet ws, int headerRow)
    {
        var row = ws.Row(headerRow);
        int last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= last; c++)
        {
            string text = Text(ws.Cell(headerRow, c)).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length == 0) continue;
            string header = text;
            for (int n = 2; !seen.Add(header); n++) header = $"{text} ({n})";
            yield return (header, c);
        }
    }

    private static int GuessHeaderRow(IXLWorksheet ws)
    {
        int last = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 30);
        int best = 0, bestCount = 0;
        for (int r = 1; r <= last; r++)
        {
            var cells = ws.Row(r).CellsUsed().Where(c => c.DataType == XLDataType.Text && c.GetString().Trim().Length > 0).ToList();
            if (cells.Count > bestCount)
            {
                best = r;
                bestCount = cells.Count;
            }
        }
        return bestCount >= 2 ? best : (last > 0 ? 1 : 0);
    }

    private static string Text(IXLCell cell)
    {
        try { return cell.GetFormattedString(); }
        catch { return cell.Value.ToString(CultureInfo.CurrentCulture); }
    }
}

/// <summary>What Fill mode will write.</summary>
public sealed class ExcelFillPlan
{
    public List<ExcelCellUpdate> Updates { get; } = new();
    public string Worksheet { get; init; } = "";
    public string? Table { get; init; }
    /// <summary>Rows Nexus has.</summary>
    public int Rows { get; init; }
    public int ChangedRows { get; set; }
    /// <summary>Rows from before that are cleared (Nexus has fewer rows now).</summary>
    public int ClearedRows { get; set; }
    /// <summary>The table's new last row when it has to grow; 0 when it is long enough.</summary>
    public int GrowToRow { get; set; }
    public bool IsEmpty => Updates.Count == 0 && GrowToRow == 0;
    public string Summary => IsEmpty ? "already up to date"
        : string.Join(", ", new[]
        {
            ChangedRows > 0 ? $"{ChangedRows} row{(ChangedRows == 1 ? "" : "s")} updated" : "",
            ClearedRows > 0 ? $"{ClearedRows} old row{(ClearedRows == 1 ? "" : "s")} cleared" : "",
            GrowToRow > 0 ? $"table extended to row {GrowToRow}" : "",
        }.Where(s => s.Length > 0));
}

/// <summary>One cell to change in the workbook.</summary>
public sealed record ExcelCellUpdate(int Row, int Column, string Value);

/// <summary>
/// A workbook write would have changed more than the intended cells. The workbook was not changed.
/// </summary>
public sealed class ExcelWriteVerificationException : InvalidOperationException
{
    public ExcelWriteVerificationException(string workbook, IReadOnlyList<FidelityIssue> issues)
        : base($"Nexus did not save '{workbook}': the check after writing found changes beyond the cells being updated, " +
               $"so the workbook was left as it was.\n\n{string.Join("\n", issues.Take(10).Select(i => "• " + i))}" +
               (issues.Count > 10 ? $"\n• … and {issues.Count - 10} more (see the Nexus log)" : ""))
    {
        Issues = issues;
    }

    public IReadOnlyList<FidelityIssue> Issues { get; }
}
