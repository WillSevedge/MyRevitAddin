using System.Text.RegularExpressions;
using Nexus.Contracts;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.Core;

/// <summary>What a dummy sheet needs to match its drawing.</summary>
public enum IndexAction { Create, MakeSheet, Rename, SetRevision, ShowInList, SetDiscipline }

/// <summary>One drawing made in another program (a DWG) next to its dummy sheet in the Revit model that holds the drawing index.</summary>
public sealed class IndexSyncRow
{
    public string Number { get; init; } = "";
    public required TableRow Drawing { get; init; }
    public TableRow? Sheet { get; init; }
    public string DrawingName { get; init; } = "";
    public string DrawingRev { get; init; } = "";
    public string SheetName { get; init; } = "";
    public string SheetRev { get; init; } = "";
    public bool IsPlaceholder { get; init; }
    /// <summary>"Appears In Sheet List"; null when the sheet does not exist (or the value was not read).</summary>
    public bool? InSheetList { get; init; }
    public string Discipline { get; init; } = "";
    /// <summary>The discipline value a new or empty dummy sheet gets (from the model's other sheets with the same prefix).</summary>
    public string NewDiscipline { get; init; } = "";
    /// <summary>The drawing has no number of its own: matched by its file name.</summary>
    public bool MatchedByFile { get; init; }
    public List<IndexAction> Actions { get; } = new();
    public bool InSync => Actions.Count == 0;
}

/// <summary>
/// Dummy sheet sync: CAD disciplines appear in the Revit drawing index through dummy sheets (placeholders or empty
/// sheets) in the model that holds the index. This lines each open DWG sheet up with its dummy sheet by number and
/// says what the dummy sheet needs: to exist, to be a real sheet (placeholders cannot show revisions), the drawing's
/// name, its revision, to appear in the sheet list, and the discipline value the index schedules sort by.
/// </summary>
public static class SheetIndexSync
{
    public const string SheetListId = "SHEET_SCHEDULED";
    public static string NameId => SheetFieldMap.ColumnId("Title");
    public static string RevisionId => SheetFieldMap.ColumnId("Revision");

    private static readonly Regex DefaultLayoutName = new(@"^Layout\s*\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRevit(TableRow row) => row.Source.Host.HostKind == HostKinds.Revit;

    /// <summary>Revit sheets (and placeholders) of the index model.</summary>
    public static IEnumerable<TableRow> IndexSheets(ResultTable table, string indexDocument) =>
        table.Rows.Where(r => IsRevit(r) && r.Depth == 0 && string.Equals(r.Source.DocumentTitle, indexDocument, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shared text parameters on the index model's sheets: candidates for the discipline the index sorts by.</summary>
    public static List<string> DisciplineParameters(ResultTable table, string indexDocument) =>
        IndexSheets(table, indexDocument)
            .SelectMany(r => r.Values.Values)
            .Where(p => p.Source == PropertySource.Shared && p.StorageType == "String" && !p.IsReadOnly)
            .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The letters a sheet number starts with: "E-101" → "E", "FP101" → "FP".</summary>
    public static string Prefix(string number)
    {
        var m = Regex.Match(number.Trim(), "^[A-Za-z]+");
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }

    public static List<IndexSyncRow> Plan(ResultTable table, string indexDocument, string? disciplineParameter)
    {
        var sheets = new Dictionary<string, TableRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in IndexSheets(table, indexDocument))
        {
            string n = ExcelCompare.NormalizeKey(Text(s, SheetFieldMap.NumberColumnId));
            if (n.Length > 0) sheets.TryAdd(n, s);
        }

        // Discipline per prefix: the value most of the model's sheets with that prefix have.
        var disciplineByPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(disciplineParameter))
            foreach (var g in sheets.Values
                         .Select(s => (Prefix: Prefix(Text(s, SheetFieldMap.NumberColumnId)), Value: Discipline(s, disciplineParameter!)))
                         .Where(x => x.Prefix.Length > 0 && x.Value.Length > 0)
                         .GroupBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase))
                disciplineByPrefix[g.Key] = g.GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Count()).First().Key;

        var result = new List<IndexSyncRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in table.Rows.Where(r => !IsRevit(r) && r.Depth == 0))
        {
            string number = Text(d, SheetFieldMap.NumberColumnId).Trim();
            bool byFile = false;
            if (number.Length == 0)
            {
                // A one-sheet DWG named after its sheet ("E-101 - POWER PLAN.dwg"), matched to a dummy sheet's number.
                var hits = sheets.Keys.Where(k => DeliverablesCompare.FileNameHas(d.Document, k)).OrderByDescending(k => k.Length).ToList();
                if (hits.Count == 0) continue;
                number = Text(sheets[hits[0]], SheetFieldMap.NumberColumnId).Trim();
                byFile = true;
            }
            string key = ExcelCompare.NormalizeKey(number);
            if (!seen.Add(key)) continue; // the same number in two drawings: the first one wins
            sheets.TryGetValue(key, out var sheet);

            string drawingName = DrawingName(d);
            string drawingRev = Rev(Text(d, RevisionId));
            string newDiscipline = disciplineByPrefix.TryGetValue(Prefix(number), out var v) ? v : "";
            var row = new IndexSyncRow
            {
                Number = number,
                Drawing = d,
                Sheet = sheet,
                DrawingName = drawingName,
                DrawingRev = drawingRev,
                SheetName = sheet is null ? "" : Text(sheet, NameId),
                SheetRev = sheet is null ? "" : Rev(Text(sheet, RevisionId)),
                IsPlaceholder = sheet?.ItemType.Contains("Placeholder", StringComparison.OrdinalIgnoreCase) == true,
                InSheetList = sheet is null ? null : InList(sheet),
                Discipline = sheet is null || string.IsNullOrWhiteSpace(disciplineParameter) ? "" : Discipline(sheet, disciplineParameter!),
                NewDiscipline = newDiscipline,
                MatchedByFile = byFile,
            };
            if (sheet is null)
            {
                row.Actions.Add(IndexAction.Create);
            }
            else
            {
                if (row.IsPlaceholder && drawingRev.Length > 0) row.Actions.Add(IndexAction.MakeSheet);
                if (drawingName.Length > 0 && !string.Equals(drawingName.Trim(), row.SheetName.Trim(), StringComparison.Ordinal)) row.Actions.Add(IndexAction.Rename);
                if (drawingRev.Length > 0 && !string.Equals(drawingRev, row.SheetRev, StringComparison.OrdinalIgnoreCase)) row.Actions.Add(IndexAction.SetRevision);
                if (row.InSheetList == false) row.Actions.Add(IndexAction.ShowInList);
                if (row.Discipline.Length == 0 && newDiscipline.Length > 0) row.Actions.Add(IndexAction.SetDiscipline);
            }
            result.Add(row);
        }
        return result.OrderBy(r => r.InSync).ThenBy(r => r.Number, NaturalComparer.Instance).ToList();
    }

    /// <summary>The request entry that brings the dummy sheet in line with its drawing.</summary>
    public static NewSheet ToRequest(IndexSyncRow row, string? disciplineParameter)
    {
        bool create = row.Actions.Contains(IndexAction.Create);
        var sheet = new NewSheet
        {
            Number = row.Number,
            Name = create || row.Actions.Contains(IndexAction.Rename) ? row.DrawingName : "",
            Update = true,
            ReplacePlaceholder = row.Actions.Contains(IndexAction.MakeSheet),
            ShowInSheetList = create || row.Actions.Contains(IndexAction.ShowInList) ? true : null,
            Revision = (create || row.Actions.Contains(IndexAction.SetRevision)) && row.DrawingRev.Length > 0 ? row.DrawingRev : null,
        };
        if ((create || row.Actions.Contains(IndexAction.SetDiscipline)) && row.NewDiscipline.Length > 0 && !string.IsNullOrWhiteSpace(disciplineParameter))
            sheet.Values[disciplineParameter!] = row.NewDiscipline;
        return sheet;
    }

    public static string Describe(IndexAction action, IndexSyncRow row) => action switch
    {
        IndexAction.Create => "Create the dummy sheet",
        IndexAction.MakeSheet => "Make the placeholder a sheet (for its revision)",
        IndexAction.Rename => $"Rename to \"{row.DrawingName}\"",
        IndexAction.SetRevision => $"Show revision {row.DrawingRev}",
        IndexAction.ShowInList => "Show in the sheet list",
        IndexAction.SetDiscipline => $"Discipline \"{row.NewDiscipline}\"",
        _ => action.ToString(),
    };

    private static string Text(TableRow row, string id) => row.Values.TryGetValue(id, out var p) ? p.Value ?? "" : "";

    /// <summary>"-" and "--" mean no revision yet.</summary>
    private static string Rev(string value)
    {
        string v = value.Trim();
        return v.Trim('-', '–', '—').Length == 0 ? "" : v;
    }

    /// <summary>The drawing's title; a default layout name ("Layout1") is not a title.</summary>
    private static string DrawingName(TableRow d)
    {
        if (!d.Values.TryGetValue(NameId, out var p)) return "";
        string v = (p.Value ?? "").Trim();
        return DefaultLayoutName.IsMatch(v) ? "" : v;
    }

    private static bool? InList(TableRow sheet)
    {
        var p = sheet.Values.Values.FirstOrDefault(x => x.Id == SheetListId);
        if (p is null) return null;
        string raw = (p.RawValue ?? p.Value ?? "").Trim();
        return raw is "1" || raw.Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string Discipline(TableRow sheet, string parameter) =>
        sheet.Values.Values.FirstOrDefault(p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "";
}
