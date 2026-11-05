using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;

namespace Nexus.Hub.ViewModels;

/// <summary>Print &amp; PDF: the sheets to send there, and the settings it remembers.</summary>
public sealed partial class MainViewModel
{
    private readonly DrawingPdfs _drawingPdfs = new();

    /// <summary>Print &amp; PDF for the selected sheets (or every sheet shown), in the order shown.</summary>
    public PrintViewModel? CreatePrint(IReadOnlyList<System.Data.DataRowView> views, bool selection)
    {
        var sheets = new List<PrintSheet>();
        var seen = new HashSet<TableRow>(ReferenceEqualityComparer.Instance);
        foreach (var view in views)
        {
            if (RowOf(view) is not { } row || !seen.Add(row)) continue;
            if (row.Source.Tag is not ResultRun run || row.ItemType is not ("Sheet" or "Layout") || row.ItemId.Length == 0) continue;
            string number = CurrentValue(row, SheetFieldMap.NumberColumnId) ?? "";
            string name = CurrentValue(row, SheetFieldMap.ColumnId("Title")) is { Length: > 0 } t ? t : row.Item.Trim();
            sheets.Add(new PrintSheet { Row = row, Run = run, Number = number.Trim(), Name = name.Trim() });
        }
        if (sheets.Count == 0) return null;
        string scope = selection ? $"{sheets.Count} selected sheets" : $"All {sheets.Count} sheets shown";
        return new PrintViewModel(this, _drawingPdfs, sheets, scope, _settings.PdfFolder, _settings.PdfNamePattern);
    }

    /// <summary>Markups: the PDFs last read, kept between sessions.</summary>
    internal List<string> MarkupPdfs
    {
        get => _settings.MarkupPdfs;
        set
        {
            _settings.MarkupPdfs = value;
            _settings.Save();
        }
    }

    /// <summary>The loaded sheets by number (Sheets view), to match markups to sheets; empty otherwise.</summary>
    internal Dictionary<string, TableRow> SheetsByNumber()
    {
        var result = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        if (_table is null || !IsSheetsView) return result;
        foreach (var row in _table.Rows)
            if (row.Values.TryGetValue(SheetFieldMap.NumberColumnId, out var n) && !string.IsNullOrWhiteSpace(n.Value))
                result.TryAdd(Nexus.Hub.Core.Excel.ExcelCompare.NormalizeKey(n.Value), row);
        return result;
    }

    /// <summary>"System", "Light" or "Dark" (⋯ › Theme).</summary>
    public string Theme
    {
        get => AppTheme.Normalize(_settings.Theme);
        set
        {
            string theme = AppTheme.Normalize(value);
            if (theme == Theme) return;
            _settings.Theme = theme;
            _settings.Save();
            AppTheme.Apply(theme);
            Raise();
        }
    }

    public void RememberPdfSettings(string folder, string pattern)
    {
        _settings.PdfFolder = folder;
        _settings.PdfNamePattern = pattern;
        _settings.Save();
    }
}
