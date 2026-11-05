using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Nexus.Hub.Core.Excel;

/// <summary>
/// Writes cell values into an existing workbook by editing only those cells (DocumentFormat.OpenXml),
/// so everything else in the file stays as Excel saved it: macros, form controls, comments, data
/// validation, conditional formatting, tables, custom XML, styles. Only the edited worksheet, the
/// shared strings and the workbook part (recalculate-on-open flag) are saved again.
/// Never inserts or deletes rows, and never overwrites a formula.
/// </summary>
internal static class ExcelCellWriter
{
    /// <summary>
    /// Applies <paramref name="values"/> (row, column → text) to <paramref name="worksheet"/> in the
    /// workbook at <paramref name="path"/>, in place. Returns, per cell reference, the value as stored
    /// in the file (shared string text, or the number as written), for verification.
    /// </summary>
    public static Dictionary<string, string> Write(string path, string worksheet, IEnumerable<(int Row, int Column, string Value)> values)
    {
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // AutoSave off: parts that were only read (styles) must not be saved again.
        using var doc = SpreadsheetDocument.Open(path, true, new OpenSettings { AutoSave = false });
        var wbPart = doc.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");
        var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault(s => string.Equals(s.Name?.Value, worksheet, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The workbook has no worksheet '{worksheet}'.");
        if (wbPart.GetPartById(sheet.Id!.Value!) is not WorksheetPart wsPart)
            throw new InvalidOperationException($"'{worksheet}' is a chart sheet, not a worksheet.");

        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>() ?? throw new InvalidDataException($"'{worksheet}' has no cell data.");
        var strings = new SharedStrings(wbPart);
        var dates = new DateStyles(wbPart);

        foreach (var (rowIndex, column, value) in values)
        {
            string reference = Reference(rowIndex, column);
            var row = GetOrAddRow(sheetData, (uint)rowIndex);
            var cell = GetOrAddCell(row, reference, column, sheetData);
            if (cell.CellFormula is not null)
                throw new InvalidOperationException($"Cell {reference} has a formula; it is not overwritten.");
            stored[reference] = SetValue(cell, value ?? "", strings, dates);
        }

        // Formulas that depend on the edited cells show their new results when the file is opened.
        var calc = wbPart.Workbook.CalculationProperties;
        if (calc is null) wbPart.Workbook.Append(calc = new CalculationProperties());
        calc.FullCalculationOnLoad = true;

        strings.Save();
        wsPart.Worksheet.Save();
        wbPart.Workbook.Save();
        return stored;
    }

    /// <summary>
    /// Makes an Excel table longer, down to <paramref name="newLastRow"/>, the way Excel does when you type below
    /// it: the table's range (and its filter range) grows, each new row gets the cell formats of the table's
    /// last row, and calculated columns get their formula. Nothing else changes.
    /// </summary>
    public static ExcelTableGrowth GrowTable(string path, string worksheet, string tableName, int newLastRow)
    {
        using var doc = SpreadsheetDocument.Open(path, true, new OpenSettings { AutoSave = false });
        var wbPart = doc.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");
        var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault(s => string.Equals(s.Name?.Value, worksheet, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The workbook has no worksheet '{worksheet}'.");
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
        var tablePart = wsPart.TableDefinitionParts.FirstOrDefault(p =>
                            string.Equals(p.Table?.Name?.Value, tableName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Table?.DisplayName?.Value, tableName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"'{worksheet}' has no table '{tableName}'.");
        var table = tablePart.Table!;
        var (firstCol, firstRow, lastCol, lastRow) = ParseRange(table.Reference!.Value!);
        bool totals = (table.TotalsRowCount?.Value ?? 0) > 0 || table.TotalsRowShown?.Value == true && (table.TotalsRowCount?.Value ?? 0) > 0;
        if (totals) throw new InvalidOperationException($"Table '{tableName}' has a totals row; make it longer in Excel instead.");
        if (newLastRow <= lastRow) return new ExcelTableGrowth(tablePart.Uri.OriginalString, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        string newRef = $"{ColumnLetters(firstCol)}{firstRow}:{ColumnLetters(lastCol)}{newLastRow}";
        table.Reference = newRef;
        if (table.AutoFilter is { } filter) filter.Reference = newRef;

        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>() ?? throw new InvalidDataException($"'{worksheet}' has no cell data.");
        var columns = table.TableColumns?.Elements<TableColumn>().ToList() ?? new List<TableColumn>();
        var formulaCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateRow = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == (uint)lastRow);
        for (int r = lastRow + 1; r <= newLastRow; r++)
        {
            var row = GetOrAddRow(sheetData, (uint)r);
            if (templateRow?.StyleIndex is not null && row.StyleIndex is null)
            {
                row.StyleIndex = templateRow.StyleIndex.Value;
                row.CustomFormat = templateRow.CustomFormat;
            }
            for (int i = 0; i < columns.Count; i++)
            {
                int column = firstCol + i;
                string reference = Reference(r, column);
                var template = templateRow?.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value == Reference(lastRow, column));
                string? formula = columns[i].CalculatedColumnFormula?.Text;
                if (template?.StyleIndex is null && formula is null) continue;
                var cell = GetOrAddCell(row, reference, column, sheetData);
                if (template?.StyleIndex is not null && cell.StyleIndex is null) cell.StyleIndex = template.StyleIndex.Value;
                if (formula is not null && cell.CellFormula is null && cell.CellValue is null)
                {
                    cell.CellFormula = new CellFormula(formula);
                    formulaCells.Add(reference);
                }
            }
        }

        // Excel works out the calculated values when the file is opened.
        var calc = wbPart.Workbook.CalculationProperties;
        if (calc is null) wbPart.Workbook.Append(calc = new CalculationProperties());
        calc.FullCalculationOnLoad = true;

        tablePart.Table.Save();
        wsPart.Worksheet.Save();
        wbPart.Workbook.Save();
        return new ExcelTableGrowth(tablePart.Uri.OriginalString, formulaCells);
    }

    private static (int FirstCol, int FirstRow, int LastCol, int LastRow) ParseRange(string reference)
    {
        var m = Regex.Match(reference, @"^\$?([A-Z]+)\$?(\d+):\$?([A-Z]+)\$?(\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidDataException($"Unexpected table range '{reference}'.");
        return (ColumnNumber(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                ColumnNumber(m.Groups[3].Value), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    private static int ColumnNumber(string letters)
    {
        int n = 0;
        foreach (char c in letters.ToUpperInvariant()) n = n * 26 + (c - 'A' + 1);
        return n;
    }

    /// <summary>A1-style reference, e.g. (4, 2) → "B4".</summary>
    public static string Reference(int row, int column) => ColumnLetters(column) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>3 → "C", 28 → "AB".</summary>
    public static string ColumnLetters(int column)
    {
        string letters = "";
        for (int c = column; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
        return letters;
    }

    // ------------------------------------------------------------------ values

    /// <summary>Keeps numbers and dates as numbers when the text is one (so formulas keep working).</summary>
    private static string SetValue(Cell cell, string value, SharedStrings strings, DateStyles dates)
    {
        cell.InlineString = null;
        if (value.Length == 0)
        {
            cell.CellValue = null;
            cell.DataType = null;
            return "";
        }

        bool hadValue = cell.CellValue is not null;
        bool wasNumber = hadValue && (cell.DataType is null || cell.DataType.Value == CellValues.Number);
        bool isDate = dates.IsDate(cell.StyleIndex?.Value ?? 0);

        if (isDate && (wasNumber || !hadValue) && DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
            return SetNumber(cell, date.ToOADate());
        if ((wasNumber || !hadValue) && !LooksLikeText(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number))
            return SetNumber(cell, number);

        cell.CellValue = new CellValue(strings.Index(value).ToString(CultureInfo.InvariantCulture));
        cell.DataType = CellValues.SharedString;
        return value;
    }

    private static string SetNumber(Cell cell, double number)
    {
        string text = number.ToString("R", CultureInfo.InvariantCulture);
        cell.CellValue = new CellValue(text);
        cell.DataType = null;
        return text;
    }

    /// <summary>"001", "A-101" style values must stay text.</summary>
    private static bool LooksLikeText(string v) =>
        (v.Length > 1 && v[0] == '0' && char.IsDigit(v[1])) || v.Any(ch => char.IsLetter(ch) && ch is not ('E' or 'e'));

    // ------------------------------------------------------------------ rows and cells

    private static Row GetOrAddRow(SheetData data, uint index)
    {
        Row? after = null;
        foreach (var row in data.Elements<Row>())
        {
            uint r = row.RowIndex?.Value ?? 0;
            if (r == index) return row;
            if (r > index) break;
            after = row;
        }
        var created = new Row { RowIndex = index };
        if (after is null) data.PrependChild(created);
        else after.InsertAfterSelf(created);
        return created;
    }

    private static Cell GetOrAddCell(Row row, string reference, int column, SheetData data)
    {
        Cell? after = null;
        foreach (var cell in row.Elements<Cell>())
        {
            int c = ColumnOf(cell.CellReference?.Value);
            if (string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase)) return cell;
            if (c > column) break;
            after = cell;
        }
        // A new cell takes the format of the cell above it, so an added row looks like the rows around it.
        var created = new Cell { CellReference = reference, StyleIndex = StyleAbove(data, row.RowIndex?.Value ?? 0, column) };
        if (after is null) row.PrependChild(created);
        else after.InsertAfterSelf(created);
        return created;
    }

    private static UInt32Value? StyleAbove(SheetData data, uint rowIndex, int column)
    {
        var above = data.Elements<Row>().LastOrDefault(r => (r.RowIndex?.Value ?? 0) < rowIndex);
        var cell = above?.Elements<Cell>().FirstOrDefault(c => ColumnOf(c.CellReference?.Value) == column);
        return cell?.StyleIndex is { } s ? new UInt32Value(s.Value) : null;
    }

    private static int ColumnOf(string? reference)
    {
        int n = 0;
        foreach (char ch in reference ?? "")
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The shared string table, created only when a text value is written and none exists.</summary>
    private sealed class SharedStrings
    {
        private readonly WorkbookPart _wb;
        private SharedStringTablePart? _part;
        private Dictionary<string, int>? _index;
        private bool _changed;

        public SharedStrings(WorkbookPart wb)
        {
            _wb = wb;
            _part = wb.SharedStringTablePart;
        }

        public int Index(string text)
        {
            if (_part is null)
            {
                _part = _wb.AddNewPart<SharedStringTablePart>();
                _part.SharedStringTable = new SharedStringTable();
                _changed = true;
            }
            var table = _part.SharedStringTable;
            if (_index is null)
            {
                _index = new Dictionary<string, int>(StringComparer.Ordinal);
                int i = 0;
                foreach (var item in table.Elements<SharedStringItem>())
                {
                    // Only plain (unformatted) entries are reused; rich text keeps its own entry.
                    if (item.Text is { } t && item.Elements<Run>().FirstOrDefault() is null) _index.TryAdd(t.Text ?? "", i);
                    i++;
                }
            }
            if (_index.TryGetValue(text, out int found)) return found;

            int at = table.Elements<SharedStringItem>().Count();
            var t2 = new Text(text);
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))) t2.Space = SpaceProcessingModeValues.Preserve;
            table.AppendChild(new SharedStringItem(t2));
            table.UniqueCount = (uint)(at + 1);
            _index[text] = at;
            _changed = true;
            return at;
        }

        public void Save()
        {
            if (_changed) _part?.SharedStringTable.Save();
        }
    }

    /// <summary>Which cell formats show dates, so a typed date is stored as a date serial number.</summary>
    private sealed class DateStyles
    {
        private static readonly Regex Literal = new("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", RegexOptions.Compiled);
        private readonly List<uint> _formatOfStyle = new();
        private readonly Dictionary<uint, string> _custom = new();

        public DateStyles(WorkbookPart wb)
        {
            var styles = wb.WorkbookStylesPart?.Stylesheet;
            if (styles is null) return;
            foreach (var f in styles.NumberingFormats?.Elements<NumberingFormat>() ?? Enumerable.Empty<NumberingFormat>())
                if (f.NumberFormatId?.Value is { } id) _custom[id] = f.FormatCode?.Value ?? "";
            foreach (var xf in styles.CellFormats?.Elements<CellFormat>() ?? Enumerable.Empty<CellFormat>())
                _formatOfStyle.Add(xf.NumberFormatId?.Value ?? 0);
        }

        public bool IsDate(uint styleIndex)
        {
            if (styleIndex >= _formatOfStyle.Count) return false;
            uint id = _formatOfStyle[(int)styleIndex];
            if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) return true;
            if (!_custom.TryGetValue(id, out var code)) return false;
            string bare = Literal.Replace(code, "");
            // Years or days mean a date ("m" alone could be minutes).
            return bare.IndexOfAny(new[] { 'y', 'Y', 'd', 'D' }) >= 0;
        }
    }
}

/// <summary>What <see cref="ExcelCellWriter.GrowTable"/> changed: the table part, and the cells given the calculated formulas.</summary>
public sealed record ExcelTableGrowth(string TablePart, ISet<string> FormulaCells);
