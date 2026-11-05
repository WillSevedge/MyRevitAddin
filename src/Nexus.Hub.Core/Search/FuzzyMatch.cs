namespace Nexus.Hub.Core.Search;

/// <summary>
/// Scores how well a typed query matches a text, for the command palette: every query character must
/// appear in order (case-insensitive). Whole-text and word-start matches, consecutive characters and
/// earlier matches score higher. Several words in the query must each match.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Higher is better; null when the text does not match.</summary>
    public static int? Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        int total = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = ScoreWord(word, text);
            if (s is null) return null;
            total += s.Value;
        }
        return total;
    }

    private static int? ScoreWord(string word, string text)
    {
        if (text.Equals(word, StringComparison.OrdinalIgnoreCase)) return 1000;
        int contains = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (contains >= 0)
        {
            bool wordStart = contains == 0 || !char.IsLetterOrDigit(text[contains - 1]);
            return (contains == 0 ? 600 : wordStart ? 450 : 300) + word.Length * 10 - Math.Min(contains, 50);
        }

        // In-order characters, e.g. "rvp" in "Review and apply".
        int score = 0, at = 0, run = 0;
        foreach (char c in word)
        {
            int found = -1;
            for (int i = at; i < text.Length; i++)
                if (char.ToUpperInvariant(text[i]) == char.ToUpperInvariant(c)) { found = i; break; }
            if (found < 0) return null;
            bool start = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            run = found == at ? run + 1 : 0;
            score += 5 + (start ? 15 : 0) + run * 4 - Math.Min(found - at, 10);
            at = found + 1;
        }
        return Math.Max(1, score);
    }
}
