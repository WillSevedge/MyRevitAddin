using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Hub.Core.Bulk;

public enum RenameMode
{
    /// <summary>Sequence from a pattern: "A1##" → A101, A102... (# = digit, padded).</summary>
    Renumber,
    /// <summary>Find and replace, with * and ? wildcards.</summary>
    Replace,
    /// <summary>UPPER, lower or Title Case.</summary>
    ChangeCase,
    /// <summary>Add a prefix and/or suffix.</summary>
    AddText,
    /// <summary>Remove a prefix and/or suffix.</summary>
    RemoveText,
}

public enum TextCase { Upper, Lower, Title }

public sealed class RenameOptions
{
    public RenameMode Mode { get; set; } = RenameMode.Renumber;

    // Renumber
    /// <summary>"A1##": the #s are the counter, zero-padded to their count. No # = counter appended.</summary>
    public string Pattern { get; set; } = "A1##";
    public int Start { get; set; } = 1;
    public int Step { get; set; } = 1;

    // Replace
    public string Find { get; set; } = "";
    public string ReplaceWith { get; set; } = "";
    public bool MatchCase { get; set; }
    /// <summary>Find must match the whole value (otherwise every occurrence is replaced).</summary>
    public bool WholeValue { get; set; }

    // Case
    public TextCase Case { get; set; } = TextCase.Upper;

    // Add / remove text
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
}

public enum RenameStatus
{
    /// <summary>Will change.</summary>
    Change,
    /// <summary>The result is the same as the current value.</summary>
    NoChange,
    /// <summary>The new value is used by another item (unique fields such as sheet numbers).</summary>
    Duplicate,
    /// <summary>The item cannot be edited (reason in Message).</summary>
    Locked,
    /// <summary>The result would be empty.</summary>
    Empty,
}

/// <summary>One item in the preview: current value → new value.</summary>
public sealed class RenamePreview
{
    public required TableRow Row { get; init; }
    public required string Old { get; init; }
    public required string New { get; init; }
    public required RenameStatus Status { get; init; }
    public string? Message { get; init; }
    /// <summary>Changes that can be staged (Change status).</summary>
    public bool CanApply => Status == RenameStatus.Change;
}

/// <summary>
/// Computes bulk renames and renumbers for a column over a list of rows (in the order shown), with a
/// preview of every result. For unique fields (sheet numbers) the preview flags values that would clash
/// with each other or with any other item that keeps its value.
/// </summary>
public static class BulkRename
{
    public static List<RenamePreview> Preview(
        IReadOnlyList<TableRow> rows,
        string columnId,
        RenameOptions options,
        Func<TableRow, string, string?> value,
        Func<TableRow, string, string?> blocker,
        bool unique,
        IReadOnlyList<TableRow>? allRows = null)
    {
        var results = new List<RenamePreview>();
        int counter = options.Start;
        var proposed = new List<(TableRow Row, string Old, string New, string? Locked)>();
        foreach (var row in rows)
        {
            if (!row.Values.ContainsKey(columnId)) continue;
            string old = value(row, columnId) ?? "";
            string? locked = blocker(row, columnId);
            string next = Apply(old, options, ref counter);
            proposed.Add((row, old, next, locked));
        }

        // Unique fields (unique per file, as Revit enforces for sheet numbers): what every other item in the
        // file keeps, plus what this batch produces.
        static string Key(TableRow r, string v) => r.Source.Result.DocumentId + "\u0001" + v.Trim();
        var renamed = new HashSet<TableRow>(proposed.Where(p => p.Locked is null && p.New != p.Old).Select(p => p.Row), ReferenceEqualityComparer.Instance);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (unique)
            foreach (var row in allRows ?? rows)
                if (!renamed.Contains(row) && row.Values.ContainsKey(columnId) && (value(row, columnId) ?? "").Trim() is { Length: > 0 } v)
                    kept.Add(Key(row, v));
        var batch = unique
            ? proposed.Where(p => renamed.Contains(p.Row)).GroupBy(p => Key(p.Row, p.New), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>();

        foreach (var (row, old, next, locked) in proposed)
        {
            RenameStatus status;
            string? message = null;
            if (locked is not null) { status = RenameStatus.Locked; message = locked; }
            else if (next == old) status = RenameStatus.NoChange;
            else if (next.Trim().Length == 0) { status = RenameStatus.Empty; message = "The result would be empty."; }
            else if (unique && (kept.Contains(Key(row, next)) || batch.GetValueOrDefault(Key(row, next)) > 1))
            {
                status = RenameStatus.Duplicate;
                message = kept.Contains(Key(row, next)) ? $"{next.Trim()} is already used in this file." : $"{next.Trim()} would be given to more than one item in this file.";
            }
            else status = RenameStatus.Change;
            results.Add(new RenamePreview { Row = row, Old = old, New = next, Status = status, Message = message });
        }
        return results;
    }

    /// <summary>The new value for one item. <paramref name="counter"/> advances for each renumbered item.</summary>
    public static string Apply(string value, RenameOptions o, ref int counter)
    {
        switch (o.Mode)
        {
            case RenameMode.Renumber:
            {
                string result = Sequence(o.Pattern, counter);
                counter += o.Step == 0 ? 1 : o.Step;
                return result;
            }
            case RenameMode.Replace:
                return Replace(value, o);
            case RenameMode.ChangeCase:
                return o.Case switch
                {
                    TextCase.Upper => value.ToUpper(CultureInfo.CurrentCulture),
                    TextCase.Lower => value.ToLower(CultureInfo.CurrentCulture),
                    _ => TitleCase(value),
                };
            case RenameMode.AddText:
                return o.Prefix + value + o.Suffix;
            case RenameMode.RemoveText:
            {
                string v = value;
                var cmp = o.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (o.Prefix.Length > 0 && v.StartsWith(o.Prefix, cmp)) v = v[o.Prefix.Length..];
                if (o.Suffix.Length > 0 && v.EndsWith(o.Suffix, cmp)) v = v[..^o.Suffix.Length];
                return v;
            }
            default:
                return value;
        }
    }

    /// <summary>"A1##", 7 → "A107"; "M-#", 12 → "M-12"; "E" (no #), 3 → "E3".</summary>
    public static string Sequence(string pattern, int n)
    {
        var match = Regex.Match(pattern, "#+");
        if (!match.Success) return pattern + n.ToString(CultureInfo.InvariantCulture);
        string digits = Math.Abs(n).ToString(CultureInfo.InvariantCulture).PadLeft(match.Length, '0');
        if (n < 0) digits = "-" + digits;
        return pattern[..match.Index] + digits + pattern[(match.Index + match.Length)..];
    }

    private static string Replace(string value, RenameOptions o)
    {
        if (o.Find.Length == 0) return value;
        var flags = o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        // * = any text, ? = one character; everything else literal.
        var sb = new StringBuilder();
        foreach (char c in o.Find)
            sb.Append(c switch { '*' => ".*?", '?' => ".", _ => Regex.Escape(c.ToString()) });
        string pattern = sb.ToString();
        if (o.WholeValue)
            return Regex.IsMatch(value, "^" + pattern.Replace(".*?", ".*", StringComparison.Ordinal) + "$", flags) ? o.ReplaceWith : value;
        // A lone "*" would match empty strings everywhere: treat it as the whole value.
        if (o.Find.All(c => c == '*')) return o.ReplaceWith;
        return Regex.Replace(value, pattern, o.ReplaceWith.Replace("$", "$$", StringComparison.Ordinal), flags);
    }

    /// <summary>"FIRST FLOOR PLAN - AREA A" → "First Floor Plan - Area A"; short words stay lower inside a title.</summary>
    private static string TitleCase(string value)
    {
        var small = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "an", "and", "at", "by", "for", "in", "of", "on", "or", "the", "to", "with" };
        var words = value.ToLower(CultureInfo.CurrentCulture).Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            if (w.Length == 0) continue;
            if (i > 0 && small.Contains(w)) continue;
            words[i] = char.ToUpper(w[0], CultureInfo.CurrentCulture) + w[1..];
        }
        return string.Join(' ', words);
    }
}
