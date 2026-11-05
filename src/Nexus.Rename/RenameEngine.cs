using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Rename;

/// <summary>Applies <see cref="RenameRules"/> to one name. Pure text: no host knowledge.</summary>
public static class RenameEngine
{
    private static readonly Regex TokenPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@" {2,}", RegexOptions.Compiled);
    private static readonly Regex CamelGap = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>
    /// The new name for <paramref name="current"/>. <paramref name="number"/> is this item's place in the
    /// numbering (from <see cref="RenamePlanner"/>); <paramref name="token"/> gives token values ({Level}...).
    /// Tokens with no value become empty and are listed in <paramref name="missing"/>.
    /// </summary>
    public static string Apply(RenameRules rules, string current, int number = 1, Func<string, string?>? token = null, ICollection<string>? missing = null)
    {
        string name = current ?? "";

        // (1) RegEx
        if (rules.Regex.IsActive)
        {
            var options = rules.Regex.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            name = Regex.Replace(name, rules.Regex.Match, rules.Regex.Replace ?? "", options, TimeSpan.FromSeconds(1));
        }

        // (2) Name
        if (rules.Name.IsActive)
        {
            name = rules.Name.Mode switch
            {
                NameMode.Remove => "",
                NameMode.Fixed => Tokens(rules.Name.Fixed, token, missing),
                NameMode.Reverse => new string(name.Reverse().ToArray()),
                _ => name,
            };
        }

        // (3) Replace
        if (rules.Replace.IsActive) name = Replace(name, rules.Replace, token, missing);

        // (4) Case
        if (rules.Case.IsActive) name = ChangeCase(name, rules.Case);

        // (5) Remove
        if (rules.Remove.IsActive) name = Remove(name, rules.Remove);

        // (6) Add
        if (rules.Add.IsActive)
        {
            var add = rules.Add;
            if (add.WordSpace) name = CamelGap.Replace(name, " ");
            if (add.Insert.Length > 0) name = InsertAt(name, Tokens(add.Insert, token, missing), add.AtPosition);
            name = Tokens(add.Prefix, token, missing) + name + Tokens(add.Suffix, token, missing);
        }

        // (7) Numbering
        if (rules.Numbering.IsActive)
        {
            var n = rules.Numbering;
            string text = FormatNumber(n.Start + (number - 1) * n.Increment, n.Style, n.Pad);
            name = n.Mode switch
            {
                NumberingMode.Prefix => name.Length == 0 ? text : text + n.Separator + name,
                NumberingMode.Suffix => name.Length == 0 ? text : name + n.Separator + text,
                NumberingMode.Insert => InsertAt(name, text, n.AtPosition),
                NumberingMode.Replace => text,
                _ => name,
            };
        }
        return name;
    }

    /// <summary>True when the item's current name passes the filter.</summary>
    public static bool Passes(FilterRule filter, string current)
    {
        if (!filter.Enabled) return true;
        bool match = false;
        foreach (var raw in (filter.Mask ?? "").Split(';'))
        {
            string mask = raw.Trim();
            if (mask.Length == 0) continue;
            var options = filter.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            string pattern = filter.UseRegex ? mask : "^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            try
            {
                if (Regex.IsMatch(current ?? "", pattern, options, TimeSpan.FromSeconds(1))) { match = true; break; }
            }
            catch (ArgumentException) { /* bad pattern: no match */ }
        }
        return match != filter.Invert;
    }

    /// <summary>"{Level} - {Name}" with the values; tokens with no value become empty.</summary>
    public static string Tokens(string text, Func<string, string?>? token, ICollection<string>? missing = null)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text ?? "";
        return TokenPattern.Replace(text, m =>
        {
            string key = m.Groups[1].Value.Trim();
            string? value = token?.Invoke(key);
            if (value is null)
            {
                if (missing is not null && !missing.Contains(key)) missing.Add(key);
                return "";
            }
            return value;
        });
    }

    /// <summary>The token names used by the rules (to show which values are needed).</summary>
    public static IEnumerable<string> TokensUsed(RenameRules rules)
    {
        var texts = new List<string>();
        if (rules.Name.IsActive && rules.Name.Mode == NameMode.Fixed) texts.Add(rules.Name.Fixed);
        if (rules.Replace.IsActive) texts.Add(rules.Replace.With);
        if (rules.Add.IsActive) texts.AddRange(new[] { rules.Add.Prefix, rules.Add.Insert, rules.Add.Suffix });
        if (rules.Numbering.IsActive) texts.Add(rules.Numbering.RestartFor);
        return texts.SelectMany(t => TokenPattern.Matches(t ?? "").Cast<Match>().Select(m => m.Groups[1].Value.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All pairs at once (so "A101|A102" → "A102|A101" swaps instead of undoing itself).</summary>
    private static string Replace(string name, ReplaceRule rule, Func<string, string?>? token, ICollection<string>? missing)
    {
        var finds = rule.Find.Split('|');
        var withs = (rule.With ?? "").Split('|');
        var pairs = new List<(string Find, string With)>();
        for (int i = 0; i < finds.Length; i++)
        {
            if (finds[i].Length == 0) continue;
            // One replacement for all finds, or one per find.
            pairs.Add((finds[i], Tokens(withs.Length == finds.Length ? withs[i] : rule.With ?? "", token, missing)));
        }
        if (pairs.Count == 0) return name;
        // Longest first, so "Level 10" wins over "Level 1".
        var ordered = pairs.Select((p, i) => (p.Find, p.With, Index: i)).OrderByDescending(p => p.Find.Length).ToList();
        string alternatives = string.Join("|", ordered.Select((p, i) => $"(?<p{i}>{Regex.Escape(p.Find)})"));
        string pattern = rule.WholeWord ? @"(?<![\p{L}\p{N}])(?:" + alternatives + @")(?![\p{L}\p{N}])" : alternatives;
        var regex = new Regex(pattern, rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        string Evaluate(Match m)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (m.Groups["p" + i].Success) return ordered[i].With;
            return m.Value;
        }
        return rule.FirstOnly ? regex.Replace(name, Evaluate, 1) : regex.Replace(name, Evaluate);
    }

    private static string ChangeCase(string name, CaseRule rule)
    {
        var culture = CultureInfo.CurrentCulture;
        string result = rule.Mode switch
        {
            CaseMode.Upper => name.ToUpper(culture),
            CaseMode.Lower => name.ToLower(culture),
            CaseMode.Title => TitleCase(name, culture),
            CaseMode.Sentence => SentenceCase(name, culture),
            _ => name,
        };
        // Exceptions: kept exactly as typed wherever they appear as a word.
        foreach (var word in (rule.Exceptions ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            result = Regex.Replace(result, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", word.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return result;
    }

    private static string TitleCase(string text, CultureInfo culture)
    {
        var sb = new StringBuilder(text.Length);
        bool start = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(start ? char.ToUpper(c, culture) : char.ToLower(c, culture));
                start = false;
            }
            else
            {
                sb.Append(c);
                // A new word after spaces and separators, not after digits or apostrophes (3rd, Owner's).
                start = !char.IsDigit(c) && c != '\'' && c != '’';
            }
        }
        return sb.ToString();
    }

    private static string SentenceCase(string text, CultureInfo culture)
    {
        string lower = text.ToLower(culture);
        int first = lower.Select((c, i) => (c, i)).FirstOrDefault(p => char.IsLetter(p.c)).i;
        if (lower.Length == 0 || !char.IsLetter(lower[first])) return lower;
        return lower.Substring(0, first) + char.ToUpper(lower[first], culture) + lower.Substring(first + 1);
    }

    private static string Remove(string name, RemoveRule r)
    {
        if (r.Crop != CropMode.None && r.CropText.Length > 0)
        {
            int at = name.IndexOf(r.CropText, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                name = r.Crop == CropMode.Before ? name.Substring(at) : name.Substring(0, at + r.CropText.Length);
        }
        if (r.From > 0 && r.From <= name.Length)
        {
            int to = r.To <= 0 || r.To > name.Length ? name.Length : Math.Max(r.To, r.From);
            name = name.Remove(r.From - 1, to - r.From + 1);
        }
        if (r.First > 0) name = r.First >= name.Length ? "" : name.Substring(r.First);
        if (r.Last > 0) name = r.Last >= name.Length ? "" : name.Substring(0, name.Length - r.Last);
        if (r.Chars.Length > 0) name = new string(name.Where(c => r.Chars.IndexOf(c) < 0).ToArray());
        foreach (var word in r.Words.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            name = Regex.Replace(name, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", "", RegexOptions.IgnoreCase);
        if (r.Digits) name = new string(name.Where(c => !char.IsDigit(c)).ToArray());
        if (r.Symbols) name = new string(name.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
        if (r.Accents) name = RemoveAccents(name);
        if (r.DoubleSpaces || r.Words.Trim().Length > 0) name = Spaces.Replace(name, " ");
        if (r.Trim) name = name.Trim();
        return name;
    }

    private static string RemoveAccents(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>1 = before the first character; -1 = at the end, -2 = before the last...</summary>
    private static string InsertAt(string name, string text, int position)
    {
        int index = position > 0 ? position - 1 : name.Length + position + 1;
        index = Math.Max(0, Math.Min(name.Length, index));
        return name.Insert(index, text);
    }

    public static string FormatNumber(int value, NumberingStyle style, int pad)
    {
        if (style == NumberingStyle.Decimal)
        {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, pad), '0');
            return value < 0 ? "-" + digits : digits;
        }
        // Letters: 1 = A ... 26 = Z, 27 = AA (like spreadsheet columns).
        if (value < 1) value = 1;
        var sb = new StringBuilder();
        for (int v = value; v > 0; v = (v - 1) / 26)
            sb.Insert(0, (char)('A' + (v - 1) % 26));
        string letters = sb.ToString();
        return style == NumberingStyle.LowerLetters ? letters.ToLowerInvariant() : letters;
    }
}
