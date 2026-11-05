using System.Globalization;
using Autodesk.Revit.DB;
using Nexus.Agent;
using Nexus.Rename;

namespace Nexus.Agent.Revit.Rename;

/// <summary>What a rename item points at in the model.</summary>
internal sealed record RenameSource(RenameTarget Target, object Item, ElementId? ElementId);

/// <summary>Result of applying: what was renamed and what Revit refused (with its reason).</summary>
internal sealed class RenameOutcome
{
    public int Renamed { get; set; }
    public List<(string Name, string Reason)> Failed { get; } = new();
}

/// <summary>
/// Reads the renameable items of a model (Revit API thread) and applies a plan as one undoable step:
/// names that free up for another item go through a temporary name first, so swaps and shifts work.
/// </summary>
internal sealed class RenameSession
{
    private readonly Document _doc;
    private readonly AgentLog _log;

    public RenameSession(Document doc, AgentLog log)
    {
        _doc = doc;
        _log = log;
    }

    public Document Document => _doc;

    /// <summary>How many items each target has (for the list on the left).</summary>
    public int Count(RenameTarget target)
    {
        try { return target.Collect(_doc).Count(); }
        catch (Exception ex)
        {
            _log.Warn($"Bulk Rename: could not list {target.Label}.", ex);
            return 0;
        }
    }

    /// <summary>The items of the chosen targets, in a natural order (by detail, then name).</summary>
    public List<RenameItem> Items(IEnumerable<RenameTarget> targets)
    {
        var items = new List<RenameItem>();
        foreach (var target in targets)
        {
            IEnumerable<object> found;
            try { found = target.Collect(_doc).ToList(); }
            catch (Exception ex)
            {
                _log.Warn($"Bulk Rename: could not list {target.Label}.", ex);
                continue;
            }
            var rows = new List<RenameItem>();
            foreach (var o in found)
            {
                try { rows.Add(ToItem(target, o)); }
                catch (Exception ex) { _log.Warn($"Bulk Rename: skipped one of {target.Label}.", ex); }
            }
            // Levels keep their elevation order; everything else by detail then name.
            items.AddRange(target.Id == "levels" ? rows : rows.OrderBy(r => r.Detail, NaturalComparer.Instance).ThenBy(r => r.Current, NaturalComparer.Instance));
        }
        return items;
    }

    private RenameItem ToItem(RenameTarget target, object o)
    {
        var element = o as Element;
        string key = element is not null ? $"{target.Id}:{element.UniqueId}" : $"{target.Id}:{(o as Workset)?.Id.IntegerValue}";
        return new RenameItem
        {
            Key = key,
            Current = target.Get(o) ?? "",
            Kind = target.Label,
            Detail = SafeDetail(target, o),
            Scope = target.Scope(o),
            ForbiddenChars = target.Forbidden,
            Locked = LockedReason(o),
            Tokens = name => Token(target, o, name),
            Tag = new RenameSource(target, o, element?.Id),
        };
    }

    private string SafeDetail(RenameTarget target, object o)
    {
        try { return target.Detail(_doc, o); }
        catch { return ""; }
    }

    /// <summary>Workshared models: an element (or workset) someone else owns cannot be renamed until they give it up.</summary>
    private string? LockedReason(object o)
    {
        if (!_doc.IsWorkshared) return null;
        try
        {
            if (o is Workset ws)
                return ws.Owner is { Length: > 0 } owner && !string.Equals(owner, _doc.Application.Username, StringComparison.OrdinalIgnoreCase)
                    ? $"Owned by {owner}." : null;
            if (o is Element e && WorksharingUtils.GetCheckoutStatus(_doc, e.Id) == CheckoutStatus.OwnedByOtherUser)
                return $"Owned by {WorksharingUtils.GetWorksharingTooltipInfo(_doc, e.Id).Owner}.";
        }
        catch { /* not available: let Revit decide when applying */ }
        return null;
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>Built-in tokens shown in the token menu (any parameter name works too).</summary>
    public static readonly string[] CommonTokens =
        { "Name", "Level", "View Type", "Sheet Number", "Sheet Name", "Category", "Family", "Type", "Scale", "Detail Number", "Id" };

    /// <summary>The value of {name} for an item: a built-in token, else a parameter of the element or of its type.</summary>
    private string? Token(RenameTarget target, object o, string name)
    {
        try
        {
            var e = o as Element;
            switch (name.ToUpperInvariant())
            {
                case "NAME": return target.Get(o);
                case "ID": return e?.Id.Value.ToString(CultureInfo.InvariantCulture);
                case "CATEGORY": return e?.Category?.Name;
                case "LEVEL":
                    return o switch
                    {
                        View v => v.GenLevel?.Name,
                        SpatialElement s => s.Level?.Name,
                        Element el when el.LevelId != ElementId.InvalidElementId => _doc.GetElement(el.LevelId)?.Name,
                        _ => null,
                    };
                case "VIEW TYPE": return o is View view ? RenameTargets.Spaced(view.ViewType.ToString()) : null;
                case "FAMILY":
                    return o switch
                    {
                        ElementType t => t.FamilyName,
                        Family f => f.Name,
                        _ => e is not null ? (_doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName : null,
                    };
                case "TYPE":
                    return o is ElementType et ? et.Name : e is not null ? _doc.GetElement(e.GetTypeId())?.Name : null;
                case "SHEET NUMBER" when o is ViewSheet sheet: return sheet.SheetNumber;
                case "SHEET NAME" when o is ViewSheet sheet2: return sheet2.Name;
            }
            if (e is null) return null;
            var p = e.LookupParameter(name) ?? (_doc.GetElement(e.GetTypeId())?.LookupParameter(name));
            if (p is null || !p.HasValue) return p is null ? null : "";
            return p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parameter names of the first items (for the token menu).</summary>
    public static List<string> ParameterNames(IEnumerable<RenameItem> items, int sample = 30)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Take(sample))
        {
            if (item.Tag is not RenameSource { Item: Element e }) continue;
            foreach (Parameter p in e.Parameters)
                if (p.Definition?.Name is { Length: > 0 } n) names.Add(n);
        }
        return names.ToList();
    }

    // ------------------------------------------------------------------ apply

    private const string TransactionName = "Nexus: Bulk Rename";

    public RenameOutcome Apply(IReadOnlyList<RenameResult> plan)
    {
        var outcome = new RenameOutcome();
        var (direct, via) = RenamePlanner.Order(plan);
        if (direct.Count + via.Count == 0) return outcome;

        using var group = new TransactionGroup(_doc, TransactionName);
        group.Start();
        var parked = new List<RenameResult>();
        if (via.Count > 0)
        {
            // 1. Names that are moving to another item get out of the way first.
            using var t = new Transaction(_doc, "Bulk Rename: temporary names");
            Begin(t);
            int n = 0;
            foreach (var r in via)
            {
                if (TrySet(r, $"zz-nexus-rename-{++n:0000}", outcome, report: true)) parked.Add(r);
            }
            t.Commit();
        }

        using (var t = new Transaction(_doc, TransactionName))
        {
            Begin(t);
            foreach (var r in direct.Concat(parked))
            {
                if (TrySet(r, r.New, outcome, report: true)) outcome.Renamed++;
                else if (parked.Contains(r)) TrySet(r, r.Item.Current, outcome, report: false); // put the old name back
            }
            t.Commit();
        }
        group.Assimilate();
        _log.Info($"Bulk Rename: {outcome.Renamed} renamed, {outcome.Failed.Count} refused.");
        return outcome;
    }

    private bool TrySet(RenameResult r, string name, RenameOutcome outcome, bool report)
    {
        var source = (RenameSource)r.Item.Tag!;
        try
        {
            source.Target.Set(_doc, source.Item, name);
            return true;
        }
        catch (Exception ex)
        {
            if (report) outcome.Failed.Add((r.Item.Current, ex.Message));
            _log.Warn($"Bulk Rename: '{r.Item.Current}' → '{name}' refused: {ex.Message}");
            return false;
        }
    }

    /// <summary>Warnings (duplicate room numbers and the like) are accepted without a dialog.</summary>
    private static void Begin(Transaction t)
    {
        t.Start();
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new AcceptWarnings());
        options.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(options);
    }

    private sealed class AcceptWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}

/// <summary>"Level 2" before "Level 10".</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
