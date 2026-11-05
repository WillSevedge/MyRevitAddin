using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Hub.Core.Health;

public enum HealthSeverity
{
    /// <summary>Wrong on a drawing set: duplicate sheet numbers, sheets without a number.</summary>
    Error,
    /// <summary>Likely a mistake: template placeholders, missing titles, a project number that differs.</summary>
    Warning,
    /// <summary>Consistency: naming pattern, capitals, stray spaces.</summary>
    Suggestion,
}

/// <summary>One finding on one sheet, with an optional one-click fix (a value to stage for that cell).</summary>
public sealed class HealthIssue
{
    public required string RuleId { get; init; }
    public required string RuleTitle { get; init; }
    public required HealthSeverity Severity { get; init; }
    public required TableRow Row { get; init; }
    /// <summary>The cell the issue is about (a Nexus column id), when there is one.</summary>
    public string? ColumnId { get; init; }
    public required string Message { get; init; }
    /// <summary>Value that fixes the issue when staged in <see cref="ColumnId"/>; null when it needs a person.</summary>
    public string? FixValue { get; init; }

    public bool CanFix => FixValue is not null && ColumnId is not null;
    /// <summary>"A101 · Floor Plan.rvt" for lists.</summary>
    public string Where => $"{(Row.Key.Length > 0 ? Row.Key : Row.Item)} · {Row.Document}";
}

/// <summary>
/// Quality checks over the sheet index (every sheet of every open Revit model and drawing), using the
/// standard sheet fields (Number, Title, Drawn By...). Values come from <c>value</c> so pending edits in the
/// grid count; fixes are only offered where <c>blocker</c> says the cell can be edited.
/// </summary>
public static class SheetHealth
{
    private static string Field(string name) => SheetFieldMap.ColumnId(name);

    /// <summary>Revit's sheet template values: a sheet still showing them was never filled in.</summary>
    private static readonly (string Field, string Placeholder)[] Placeholders =
    {
        ("Drawn By", "Author"), ("Checked By", "Checker"), ("Designed By", "Designer"), ("Approved By", "Approver"),
        ("Issue Date", "Issue Date"), ("Project Number", "Project Number"),
    };

    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    public static List<HealthIssue> Check(IReadOnlyList<TableRow> rows, Func<TableRow, string, string?> value, Func<TableRow, string, string?> blocker)
    {
        var issues = new List<HealthIssue>();
        // Only sheets: rows that have a sheet number column at all (layouts and Revit sheets).
        var sheets = rows.Where(r => r.Values.ContainsKey(Field(SheetFieldMap.NumberField))).ToList();
        if (sheets.Count == 0) return issues;

        string Get(TableRow r, string field) => (value(r, Field(field)) ?? "");
        bool Has(TableRow r, string field) => r.Values.ContainsKey(Field(field));
        string? Fix(TableRow r, string field, string fix) => blocker(r, Field(field)) is null ? fix : null;

        // 1. Sheet numbers: missing, and duplicated across every open file.
        foreach (var r in sheets.Where(r => Get(r, SheetFieldMap.NumberField).Trim().Length == 0))
            issues.Add(new HealthIssue
            {
                RuleId = "number-missing", RuleTitle = "Sheet has no number", Severity = HealthSeverity.Error, Row = r,
                ColumnId = Field(SheetFieldMap.NumberField), Message = "This sheet has no sheet number.",
            });
        foreach (var dup in sheets.Where(r => Get(r, SheetFieldMap.NumberField).Trim().Length > 0)
                     .GroupBy(r => Normalize(Get(r, SheetFieldMap.NumberField)), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var r in dup)
            {
                var others = dup.Where(o => o != r).Select(o => o.Document == r.Document ? "this file" : o.Document).Distinct().ToList();
                issues.Add(new HealthIssue
                {
                    RuleId = "number-duplicate", RuleTitle = "Duplicate sheet numbers", Severity = HealthSeverity.Error, Row = r,
                    ColumnId = Field(SheetFieldMap.NumberField),
                    Message = $"{Get(r, SheetFieldMap.NumberField).Trim()} is also used in {string.Join(", ", others)}.",
                });
            }
        }

        // 2. Titles.
        foreach (var r in sheets.Where(r => Has(r, "Title") && Get(r, "Title").Trim().Length == 0))
            issues.Add(new HealthIssue
            {
                RuleId = "title-missing", RuleTitle = "Sheet has no name", Severity = HealthSeverity.Warning, Row = r,
                ColumnId = Field("Title"), Message = "This sheet has no sheet name.",
            });

        // 3. Template placeholders never filled in (Revit's Author / Checker / Designer / Approver...).
        foreach (var (field, placeholder) in Placeholders)
            foreach (var r in sheets.Where(r => Has(r, field) && Get(r, field).Trim().Equals(placeholder, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new HealthIssue
                {
                    RuleId = "placeholder", RuleTitle = "Template placeholder values", Severity = HealthSeverity.Warning, Row = r,
                    ColumnId = Field(field), Message = $"{SheetFieldMap.DisplayName(field)} still says \"{Get(r, field).Trim()}\" (the template's placeholder).",
                });

        // 4. Project number: the same on every sheet of a file.
        foreach (var file in sheets.Where(r => Has(r, "Project Number")).GroupBy(r => r.Document))
        {
            var majority = Majority(file.Select(r => Get(r, "Project Number").Trim()).Where(v => v.Length > 0
                && !v.Equals("Project Number", StringComparison.OrdinalIgnoreCase)), file.Count());
            if (majority is null) continue;
            foreach (var r in file.Where(r => Get(r, "Project Number").Trim() is { Length: > 0 } v && v != majority
                                              && !v.Equals("Project Number", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new HealthIssue
                {
                    RuleId = "project-number", RuleTitle = "Project number differs", Severity = HealthSeverity.Warning, Row = r,
                    ColumnId = Field("Project Number"),
                    Message = $"Project number is {Get(r, "Project Number").Trim()}; the other sheets in this file use {majority}.",
                    FixValue = Fix(r, "Project Number", majority),
                });
        }

        // 5. Revision without a date.
        foreach (var r in sheets.Where(r => Has(r, "Revision") && Has(r, "Revision Date")
                                            && Get(r, "Revision").Trim().Length > 0 && Get(r, "Revision Date").Trim().Length == 0))
            issues.Add(new HealthIssue
            {
                RuleId = "revision-date", RuleTitle = "Revision without a date", Severity = HealthSeverity.Warning, Row = r,
                ColumnId = Field("Revision Date"), Message = $"Revision {Get(r, "Revision").Trim()} has no date.",
            });

        // 6. Numbering pattern: e.g. most sheets are A101 and this one is A-101.
        var shapes = sheets.Select(r => Get(r, SheetFieldMap.NumberField).Trim()).Where(n => n.Length > 0).Select(Shape).ToList();
        var mainShape = Majority(shapes, shapes.Count, share: 0.6);
        if (mainShape is not null && shapes.Count >= 4)
        {
            string example = sheets.Select(r => Get(r, SheetFieldMap.NumberField).Trim()).First(n => Shape(n) == mainShape);
            foreach (var r in sheets)
            {
                string number = Get(r, SheetFieldMap.NumberField).Trim();
                if (number.Length == 0 || Shape(number) == mainShape) continue;
                // A fix only when dropping or adding separators gives the usual pattern (A-101 → A101).
                string? fix = Reshape(number, mainShape);
                issues.Add(new HealthIssue
                {
                    RuleId = "number-pattern", RuleTitle = "Sheet number pattern", Severity = HealthSeverity.Suggestion, Row = r,
                    ColumnId = Field(SheetFieldMap.NumberField),
                    Message = $"{number} does not follow the pattern most sheets use (like {example}).",
                    FixValue = fix is null || sheets.Any(o => Normalize(Get(o, SheetFieldMap.NumberField)) == Normalize(fix))
                        ? null : Fix(r, SheetFieldMap.NumberField, fix),
                });
            }
        }

        // 7. Titles in capitals when most are.
        var titles = sheets.Where(r => Has(r, "Title")).Select(r => Get(r, "Title").Trim()).Where(t => t.Any(char.IsLetter)).ToList();
        if (titles.Count >= 4 && titles.Count(IsUpper) >= titles.Count * 0.7)
            foreach (var r in sheets.Where(r => Has(r, "Title")))
            {
                string title = Get(r, "Title").Trim();
                if (!title.Any(char.IsLetter) || IsUpper(title)) continue;
                issues.Add(new HealthIssue
                {
                    RuleId = "title-case", RuleTitle = "Sheet name capitals", Severity = HealthSeverity.Suggestion, Row = r,
                    ColumnId = Field("Title"), Message = $"\"{title}\" is not in capitals like the other sheet names.",
                    FixValue = Fix(r, "Title", Get(r, "Title").ToUpperInvariant()),
                });
            }

        // 8. Stray spaces in any standard field.
        foreach (var r in sheets)
            foreach (var (columnId, _) in r.Values.Where(v => v.Key.StartsWith(SheetFieldMap.Group + " ›", StringComparison.Ordinal)))
            {
                string text = value(r, columnId) ?? "";
                string clean = Spaces.Replace(text.Trim(), " ");
                if (text == clean || text.Trim().Length == 0) continue;
                issues.Add(new HealthIssue
                {
                    RuleId = "spaces", RuleTitle = "Extra spaces", Severity = HealthSeverity.Suggestion, Row = r, ColumnId = columnId,
                    Message = $"{SheetFieldMap.DisplayName(columnId[(columnId.IndexOf('›') + 1)..].Trim())} has extra spaces: \"{text}\".",
                    FixValue = blocker(r, columnId) is null ? clean : null,
                });
            }

        return issues.OrderBy(i => i.Severity).ThenBy(i => i.RuleTitle, StringComparer.Ordinal)
            .ThenBy(i => i.Row.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"A-101" → "A-999"... letters become A, digits 9, everything else stays.</summary>
    public static string Shape(string number)
    {
        var sb = new StringBuilder(number.Length);
        foreach (char c in number.Trim().ToUpperInvariant())
            sb.Append(char.IsLetter(c) ? 'A' : char.IsDigit(c) ? '9' : c);
        return sb.ToString();
    }

    /// <summary>The number with its letters and digits laid into <paramref name="shape"/>'s separators, if they fit.</summary>
    private static string? Reshape(string number, string shape)
    {
        var chars = number.Trim().Where(char.IsLetterOrDigit).ToList();
        var sb = new StringBuilder();
        int i = 0;
        foreach (char s in shape)
        {
            if (s is 'A' or '9')
            {
                if (i >= chars.Count) return null;
                char c = chars[i++];
                if ((s == 'A') != char.IsLetter(c)) return null;
                sb.Append(c);
            }
            else sb.Append(s);
        }
        return i == chars.Count ? sb.ToString() : null;
    }

    private static string Normalize(string number) => number.Trim().ToUpperInvariant();

    private static bool IsUpper(string text) => !text.Any(char.IsLower);

    /// <summary>The value used by at least <paramref name="share"/> of <paramref name="total"/> items, if any.</summary>
    private static string? Majority(IEnumerable<string> values, int total, double share = 0.5)
    {
        var top = values.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).FirstOrDefault();
        return top is not null && top.Count() > total * share && top.Count() >= 2 ? top.Key : null;
    }
}
