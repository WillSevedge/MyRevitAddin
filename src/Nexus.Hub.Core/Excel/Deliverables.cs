using System.Globalization;
using ClosedXML.Excel;

namespace Nexus.Hub.Core.Excel;

/// <summary>One drawing or document on the workbook's deliverables list.</summary>
public sealed class Deliverable
{
    public int Row { get; init; }
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string Rev { get; init; } = "";
    public string Date { get; init; } = "";
    public string Status { get; init; } = "";
    public string Discipline { get; init; } = "";
    public string Type { get; init; } = "";
}

/// <summary>What Nexus reads from the linked workbook: the deliverables list, and where Model Export is.</summary>
public sealed class DeliverablesWorkbook
{
    public string Path { get; init; } = "";
    public string ListSheet { get; set; } = "";
    public List<Deliverable> Items { get; } = new();
    /// <summary>Rows on the list without a drawing number (documents not numbered yet): not compared.</summary>
    public int Unnumbered { get; set; }
    /// <summary>The table Nexus fills (Model Export); null when the workbook has none.</summary>
    public ExcelTableInfo? ModelExport { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads the deliverables list of a workbook such as the Budget Tool (the workbook is the source of truth: Nexus
/// only reads it, and only ever writes its Model Export table). The list is found by its headers (Dwg/Doc No.,
/// Rev., Date, Status, Drawing Title...), preferably on a worksheet called "Deliverables List". Values are the ones
/// Excel last calculated and saved, so lists built by formulas read as they show in Excel.
/// </summary>
public static class Deliverables
{
    public const string ModelExportTable = "ModelExport";

    private static readonly string[] NumberHeaders = { "Dwg/Doc No.", "Dwg/Doc No", "Dwg No", "Dwg No.", "Drawing No", "Drawing Number", "Sheet Number", "Sheet No", "Doc No", "Document No", "Number" };
    private static readonly string[] TitleHeaders = { "Drawing Title / Document Description", "Drawing Title", "Title", "Sheet Name", "Sheet Title", "Description", "Document Description" };
    private static readonly string[] RevHeaders = { "Rev.", "Rev", "Revision", "Current Revision" };
    private static readonly string[] DateHeaders = { "Date", "Rev Date", "Revision Date", "Issue Date" };
    private static readonly string[] StatusHeaders = { "Status", "Dwg Status", "Drawing Status", "Sheet Status" };
    private static readonly string[] DisciplineHeaders = { "Discipline" };
    private static readonly string[] TypeHeaders = { "Type" };

    /// <summary>Stop after this many rows in a row with nothing in them.</summary>
    private const int MaxBlankRows = 30;

    public static DeliverablesWorkbook Read(string path)
    {
        using var wb = OpenForRead(path);
        var result = new DeliverablesWorkbook { Path = path };

        foreach (var ws in wb.Worksheets)
            foreach (var t in ws.Tables)
                if (string.Equals(t.Name, ModelExportTable, StringComparison.OrdinalIgnoreCase)
                    || (result.ModelExport is null && string.Equals(ws.Name, "Model Export", StringComparison.OrdinalIgnoreCase)))
                    result.ModelExport = ExcelWorkbook.TableInfoOf(ws, t);

        var candidates = wb.Worksheets.OrderByDescending(ws => string.Equals(ws.Name, "Deliverables List", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(ws => ws.Name.Contains("Deliverable", StringComparison.OrdinalIgnoreCase));
        foreach (var ws in candidates)
        {
            if (result.ModelExport is { } me && me.Worksheet == ws.Name) continue;
            if (FindHeaders(ws) is not { } h) continue;
            result.ListSheet = ws.Name;
            ReadList(ws, h, result);
            return result;
        }
        result.Warnings.Add("No deliverables list was found (a worksheet with a 'Dwg/Doc No.' or 'Sheet Number' header).");
        return result;
    }

    private sealed record HeaderColumns(int Row, int Number, int Title, int Rev, int Date, int Status, int Discipline, int Type);

    /// <summary>The header row and columns of the list (first 40 rows; the first match from the left wins).</summary>
    private static HeaderColumns? FindHeaders(IXLWorksheet ws)
    {
        int lastRow = Math.Min(40, ws.LastRowUsed()?.RowNumber() ?? 0);
        int lastColumn = Math.Min(60, ws.LastColumnUsed()?.ColumnNumber() ?? 0);
        for (int r = 1; r <= lastRow; r++)
        {
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int c = 1; c <= lastColumn; c++)
            {
                string text = CellText(ws.Cell(r, c)).Trim();
                if (text.Length > 0) names.TryAdd(SheetFieldMap.Normalize(text), c);
            }
            int Find(string[] options) => options.Select(o => names.TryGetValue(SheetFieldMap.Normalize(o), out int c) ? c : 0).FirstOrDefault(c => c > 0);
            int number = Find(NumberHeaders);
            if (number == 0) continue;
            int title = Find(TitleHeaders), rev = Find(RevHeaders);
            if (title == 0 && rev == 0) continue; // a lone "Number" is not a list
            return new HeaderColumns(r, number, title, rev, Find(DateHeaders), Find(StatusHeaders), Find(DisciplineHeaders), Find(TypeHeaders));
        }
        return null;
    }

    private static void ReadList(IXLWorksheet ws, HeaderColumns h, DeliverablesWorkbook result)
    {
        int last = ws.LastRowUsed()?.RowNumber() ?? h.Row;
        int blanks = 0;
        string At(int r, int c) => c == 0 ? "" : CellText(ws.Cell(r, c)).Trim();
        for (int r = h.Row + 1; r <= last; r++)
        {
            string number = At(r, h.Number), title = At(r, h.Title);
            if (number.Length == 0 && title.Length == 0)
            {
                if (++blanks >= MaxBlankRows) break;
                continue;
            }
            blanks = 0;
            if (number.Length == 0)
            {
                result.Unnumbered++;
                continue;
            }
            result.Items.Add(new Deliverable
            {
                Row = r, Number = number, Title = title, Rev = At(r, h.Rev), Date = At(r, h.Date), Status = At(r, h.Status),
                Discipline = At(r, h.Discipline), Type = At(r, h.Type),
            });
        }
    }

    /// <summary>What Excel shows: the saved result for formulas (also for cells filled by a spilled formula).</summary>
    private static string CellText(IXLCell cell)
    {
        try
        {
            if (cell.HasFormula)
            {
                var cached = cell.CachedValue;
                if (cached.IsError) return "";
                if (cached.IsDateTime) return cached.GetDateTime().ToString("d", CultureInfo.CurrentCulture);
                if (cached.IsNumber && cell.Style.DateFormat.Format is { Length: > 0 } && LooksLikeDate(cell))
                    return DateTime.FromOADate(cached.GetNumber()).ToString("d", CultureInfo.CurrentCulture);
                return cached.ToString(CultureInfo.CurrentCulture);
            }
            var value = cell.Value;
            if (value.IsError) return "";
            return cell.GetFormattedString();
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksLikeDate(IXLCell cell)
    {
        string f = cell.Style.NumberFormat.Format ?? "";
        int id = cell.Style.NumberFormat.NumberFormatId;
        return id is >= 14 and <= 22 || (f.Length > 0 && !f.Contains('@') && f.IndexOfAny(new[] { 'd', 'y' }) >= 0);
    }

    private static XLWorkbook OpenForRead(string path)
    {
        // FileShare.ReadWrite: read even while Excel has the workbook open (the last saved version).
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var memory = new MemoryStream();
        using (stream) stream.CopyTo(memory);
        memory.Position = 0;
        return new XLWorkbook(memory);
    }

    // ------------------------------------------------------------------ Model Export link

    /// <summary>
    /// The fixed link Nexus fills Model Export with: one row per sheet (Fill), Source = program, the sheet fields by
    /// header name, formula columns left to Excel. Nexus → Excel only.
    /// </summary>
    public static ExcelLink ModelExportLink(string path, ExcelTableInfo table, ResultTable model, SheetFieldMap fields)
    {
        var columns = ExcelCompare.AutoMap(table.Headers, model, fields);
        foreach (var c in columns)
        {
            c.Direction = ExcelDirection.ToExcel;
            if (table.FormulaHeaders.Contains(c.Header))
            {
                c.IsFormula = true;
                c.ColumnId = "";
            }
        }
        return new ExcelLink
        {
            WorkbookPath = path,
            Worksheet = table.Worksheet,
            Table = table.Name,
            HeaderRow = table.HeaderRow,
            Mode = ExcelLinkMode.Fill,
            KeyColumnId = SheetFieldMap.NumberColumnId,
            Columns = columns,
        };
    }
}

// ------------------------------------------------------------------ comparison

public enum DeliverableResult { Match, Differs, NotInModel, NotOnList }

/// <summary>One drawing number, as the list has it and as the model has it.</summary>
public sealed class DeliverableComparison
{
    public string Number { get; init; } = "";
    public Deliverable? List { get; init; }
    public TableRow? Model { get; init; }
    public string ModelTitle { get; init; } = "";
    public string ModelRev { get; init; } = "";
    public string ModelDate { get; init; } = "";
    public string ModelStatus { get; init; } = "";
    public string Program { get; init; } = "";
    public string File { get; init; } = "";
    public bool TitleDiffers { get; init; }
    public bool RevDiffers { get; init; }
    public bool DateDiffers { get; init; }
    public bool StatusDiffers { get; init; }
    /// <summary>The model sheet has no number: it was matched by its file name (one-sheet DWGs named after the sheet).</summary>
    public bool MatchedByFile { get; init; }
    public DeliverableResult Result { get; init; }
}

public static class DeliverablesCompare
{
    public static string TitleId => SheetFieldMap.ColumnId("Title");
    public static string RevId => SheetFieldMap.ColumnId("Revision");
    public static string DateId => SheetFieldMap.ColumnId("Revision Date");
    public static string StatusId => SheetFieldMap.ColumnId("Status");

    /// <summary>
    /// Lines the list up with the model's sheets by number. A field only differs when both sides have a value for it
    /// (a list without a Status column, or a model without dates, is not a difference).
    /// </summary>
    public static List<DeliverableComparison> Compare(IEnumerable<Deliverable> list, ResultTable model)
    {
        var byNumber = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        var unnumbered = new List<TableRow>();
        foreach (var row in model.Rows)
            if (row.Values.TryGetValue(SheetFieldMap.NumberColumnId, out var n) && !string.IsNullOrWhiteSpace(n.Value))
                byNumber.TryAdd(ExcelCompare.NormalizeKey(n.Value), row);
            else
                unnumbered.Add(row);
        // Sheets without a number (a DWG whose title block has no number attribute) are matched by file name when the
        // file holds that one sheet: "C-101.dwg", "C-101 - SITE PLAN.dwg".
        var singleSheetFiles = unnumbered.GroupBy(r => r.Document, StringComparer.OrdinalIgnoreCase)
            .Where(g => model.Rows.Count(r => string.Equals(r.Document, g.Key, StringComparison.OrdinalIgnoreCase)) == 1)
            .Select(g => g.First()).ToList();
        var byFile = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.OrderByDescending(i => i.Number.Length)) // longest numbers first: C-101A before C-101
        {
            string key = ExcelCompare.NormalizeKey(item.Number);
            if (key.Length == 0 || byNumber.ContainsKey(key) || byFile.ContainsKey(key)) continue;
            var found = singleSheetFiles.Where(r => !byFile.ContainsValue(r) && FileNameHas(r.Document, key)).ToList();
            if (found.Count == 1) byFile[key] = found[0];
        }

        string Value(TableRow? row, string id) => row is not null && row.Values.TryGetValue(id, out var v) ? v.Value ?? "" : "";
        bool Differs(string listValue, string modelValue, bool modelHasField) =>
            modelHasField && listValue.Trim().Length + modelValue.Trim().Length > 0 && !ExcelCompare.SameValue(modelValue, listValue);

        var result = new List<DeliverableComparison>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            string key = ExcelCompare.NormalizeKey(item.Number);
            if (!seen.Add(key)) continue;
            bool byName = false;
            if (!byNumber.TryGetValue(key, out var row) && byFile.TryGetValue(key, out row)) byName = true;
            if (row is null)
            {
                result.Add(new DeliverableComparison { Number = item.Number, List = item, Result = DeliverableResult.NotInModel });
                continue;
            }
            bool Has(string id) => row.Values.ContainsKey(id);
            var c = new DeliverableComparison
            {
                Number = item.Number, List = item, Model = row,
                ModelTitle = Value(row, TitleId), ModelRev = Value(row, RevId), ModelDate = Value(row, DateId), ModelStatus = Value(row, StatusId),
                Program = ExcelSources.ProgramOf(row), File = row.Document,
                TitleDiffers = Differs(item.Title, Value(row, TitleId), Has(TitleId)),
                RevDiffers = Differs(item.Rev, Value(row, RevId), Has(RevId)),
                DateDiffers = item.Date.Length > 0 && Differs(item.Date, Value(row, DateId), Has(DateId)),
                StatusDiffers = item.Status.Length > 0 && Differs(item.Status, Value(row, StatusId), Has(StatusId)),
                MatchedByFile = byName,
            };
            result.Add(Copy(c, c.TitleDiffers || c.RevDiffers || c.DateDiffers || c.StatusDiffers ? DeliverableResult.Differs : DeliverableResult.Match));
        }
        foreach (var (key, row) in byNumber)
        {
            if (seen.Contains(key)) continue;
            result.Add(new DeliverableComparison
            {
                Number = row.Values[SheetFieldMap.NumberColumnId].Value ?? key, Model = row,
                ModelTitle = Value(row, TitleId), ModelRev = Value(row, RevId), ModelDate = Value(row, DateId), ModelStatus = Value(row, StatusId),
                Program = ExcelSources.ProgramOf(row), File = row.Document, Result = DeliverableResult.NotOnList,
            });
        }
        // Sheets without a number that no list drawing matched: shown, so they are not silently left out.
        foreach (var row in unnumbered)
        {
            if (byFile.ContainsValue(row)) continue;
            result.Add(new DeliverableComparison
            {
                Number = $"({Path.GetFileNameWithoutExtension(row.Document)})", Model = row,
                ModelTitle = Value(row, TitleId), ModelRev = Value(row, RevId), ModelDate = Value(row, DateId), ModelStatus = Value(row, StatusId),
                Program = ExcelSources.ProgramOf(row), File = row.Document, Result = DeliverableResult.NotOnList,
            });
        }
        return result;
    }

    /// <summary>The file name holds the drawing number as a whole token ("C-101 - Site.dwg" has C-101, not C-10).</summary>
    public static bool FileNameHas(string document, string number)
    {
        string name = Path.GetFileNameWithoutExtension(document);
        for (int i = name.IndexOf(number, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = name.IndexOf(number, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            bool startOk = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
            int end = i + number.Length;
            bool endOk = end == name.Length || !char.IsLetterOrDigit(name[end]);
            if (startOk && endOk) return true;
        }
        return false;
    }

    private static DeliverableComparison Copy(DeliverableComparison c, DeliverableResult r) => new()
    {
        Number = c.Number, List = c.List, Model = c.Model, ModelTitle = c.ModelTitle, ModelRev = c.ModelRev, ModelDate = c.ModelDate,
        ModelStatus = c.ModelStatus, Program = c.Program, File = c.File, TitleDiffers = c.TitleDiffers, RevDiffers = c.RevDiffers,
        DateDiffers = c.DateDiffers, StatusDiffers = c.StatusDiffers, MatchedByFile = c.MatchedByFile, Result = r,
    };
}
