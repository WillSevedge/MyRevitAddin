namespace Nexus.Rename;

/// <summary>Something that can be renamed (a view, a sheet number, a level, a type...).</summary>
public sealed class RenameItem
{
    /// <summary>Unique key (host id plus field), used to match results back.</summary>
    public required string Key { get; init; }
    public required string Current { get; init; }
    /// <summary>"View", "Sheet Number", "Level"... for the list.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Extra detail for the list (view type, family, level...).</summary>
    public string Detail { get; init; } = "";
    /// <summary>
    /// Names must be unique within this scope ("view:FloorPlan", "level", "type:123"...);
    /// null when duplicates are allowed (room names, sheet names...).
    /// </summary>
    public string? Scope { get; init; }
    /// <summary>Characters the program does not allow in this name.</summary>
    public string ForbiddenChars { get; init; } = "";
    /// <summary>Why it cannot be renamed (another user owns it...), or null.</summary>
    public string? Locked { get; init; }
    /// <summary>Token values ({Level}, {Sheet Number}...); null when it has none.</summary>
    public Func<string, string?>? Tokens { get; init; }
    /// <summary>The host's own object (element id...), for applying.</summary>
    public object? Tag { get; init; }
}

public enum RenameStatus
{
    /// <summary>Will be renamed.</summary>
    Rename,
    /// <summary>The rules give the same name.</summary>
    Unchanged,
    /// <summary>Not taking part (unticked or filtered out).</summary>
    Skipped,
    /// <summary>The new name has characters the program does not allow.</summary>
    Invalid,
    /// <summary>The new name would be empty.</summary>
    Empty,
    /// <summary>The new name is already used in the same scope.</summary>
    Duplicate,
    /// <summary>The item cannot be renamed.</summary>
    Locked,
}

public sealed class RenameResult
{
    public required RenameItem Item { get; init; }
    public required string New { get; init; }
    public required RenameStatus Status { get; init; }
    public string? Message { get; init; }
    public bool WillRename => Status == RenameStatus.Rename;
}

/// <summary>
/// Works out every new name (numbering in list order), and checks them: allowed characters, empty names,
/// and names that would be used twice in the same scope (by renamed items, or by items keeping their name,
/// including <c>others</c>: names in use that are not in the list).
/// </summary>
public static class RenamePlanner
{
    public static List<RenameResult> Plan(IReadOnlyList<RenameItem> items, RenameRules rules,
        Func<RenameItem, bool>? included = null, IEnumerable<(string Scope, string Name)>? others = null)
    {
        var results = new List<RenameResult>(items.Count);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 1. New names.
        var draft = new List<(RenameItem Item, string New, RenameStatus Status, string? Message)>();
        foreach (var item in items)
        {
            if (!(included?.Invoke(item) ?? true) || !RenameEngine.Passes(rules.Filter, item.Current))
            {
                draft.Add((item, item.Current, RenameStatus.Skipped, null));
                continue;
            }
            // Numbering restarts for each value of RestartFor ({Level}...), counting only taking-part items.
            string group = rules.Numbering.IsActive && rules.Numbering.RestartFor.Trim().Length > 0
                ? RenameEngine.Tokens(rules.Numbering.RestartFor, item.Tokens) : "";
            counters.TryGetValue(group, out int count);
            counters[group] = ++count;

            var missing = new List<string>();
            string name;
            try
            {
                name = RenameEngine.Apply(rules, item.Current, count, item.Tokens, missing);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                draft.Add((item, item.Current, RenameStatus.Invalid, "The regular expression is not valid: " + ex.Message));
                continue;
            }
            string? note = missing.Count > 0 ? $"This item has no value for {string.Join(", ", missing.Select(m => "{" + m + "}"))} (left blank)." : null;

            if (name == item.Current) draft.Add((item, name, RenameStatus.Unchanged, note));
            else if (item.Locked is not null) draft.Add((item, name, RenameStatus.Locked, item.Locked));
            else if (name.Trim().Length == 0) draft.Add((item, name, RenameStatus.Empty, "The new name would be empty."));
            else if (item.ForbiddenChars.Length > 0 && name.IndexOfAny(item.ForbiddenChars.ToCharArray()) >= 0)
            {
                var bad = name.Where(c => item.ForbiddenChars.IndexOf(c) >= 0).Distinct();
                draft.Add((item, name, RenameStatus.Invalid, $"Not allowed in this name: {string.Join(" ", bad)}"));
            }
            else draft.Add((item, name, RenameStatus.Rename, note));
        }

        // 2. Names used twice in a scope: what each item will be called afterwards, plus names not in the list.
        var final = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var outside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, name) in others ?? Enumerable.Empty<(string, string)>()) outside.Add(scope + "\u0001" + name);
        for (int i = 0; i < draft.Count; i++)
        {
            var d = draft[i];
            if (d.Item.Scope is null) continue;
            string key = d.Item.Scope + "\u0001" + (d.Status == RenameStatus.Rename ? d.New : d.Item.Current);
            if (!final.TryGetValue(key, out var list)) final[key] = list = new List<int>();
            list.Add(i);
        }
        for (int i = 0; i < draft.Count; i++)
        {
            var d = draft[i];
            if (d.Status != RenameStatus.Rename || d.Item.Scope is null) goto add;
            string key = d.Item.Scope + "\u0001" + d.New;
            var users = final[key];
            if (users.Count > 1)
            {
                var other = draft[users.First(u => u != i)];
                string message = other.Status == RenameStatus.Rename
                    ? $"Another item would also be called \"{d.New}\" ({other.Item.Current})."
                    : $"\"{d.New}\" is already used by {(other.Item.Kind.Length > 0 ? other.Item.Kind.ToLowerInvariant() + " " : "")}\"{other.Item.Current}\".";
                results.Add(new RenameResult { Item = d.Item, New = d.New, Status = RenameStatus.Duplicate, Message = message });
                continue;
            }
            if (outside.Contains(key))
            {
                results.Add(new RenameResult { Item = d.Item, New = d.New, Status = RenameStatus.Duplicate, Message = $"\"{d.New}\" is already used in the model." });
                continue;
            }
        add:
            results.Add(new RenameResult { Item = d.Item, New = d.New, Status = d.Status, Message = d.Message });
        }
        return results;
    }

    /// <summary>
    /// The order to apply renames in so no rename hits a name still in use: items whose new name is the
    /// current name of another renamed item in the same scope need a temporary name first (swaps, shifts).
    /// </summary>
    public static (List<RenameResult> Direct, List<RenameResult> ViaTemporary) Order(IEnumerable<RenameResult> results)
    {
        var renames = results.Where(r => r.WillRename).ToList();
        var current = new HashSet<string>(renames.Where(r => r.Item.Scope is not null).Select(r => r.Item.Scope + "\u0001" + r.Item.Current),
            StringComparer.OrdinalIgnoreCase);
        var via = renames.Where(r => r.Item.Scope is not null && current.Contains(r.Item.Scope + "\u0001" + r.New)).ToList();
        var direct = renames.Except(via).ToList();
        return (direct, via);
    }
}
