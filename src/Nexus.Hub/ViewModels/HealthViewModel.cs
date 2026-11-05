using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Health;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// Sheet health: runs the checks over the loaded sheets whenever the data changes (edits included),
/// lists the findings by check, marks each sheet in the grid, and stages one-click fixes as ordinary
/// edits (they go through Review &amp; apply like any other change).
/// </summary>
public sealed class HealthViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly Dictionary<int, List<HealthIssue>> _byRow = new();
    private List<HealthIssue> _issues = new();
    private bool _available;
    private bool _isOpen;
    private bool _recheckQueued;
    private int _version;
    private int _sheetCount;

    public HealthViewModel(MainViewModel main)
    {
        _main = main;
        _main.DataChanged += QueueRecheck;
        FixAllCommand = new RelayCommand(() => { FixAll(_issues); return Task.CompletedTask; }, () => FixableCount > 0);
    }

    public ObservableCollection<HealthGroup> Groups { get; } = new();
    public RelayCommand FixAllCommand { get; }

    /// <summary>The checks apply to the Sheets view (every sheet of every open file).</summary>
    public bool IsAvailable
    {
        get => _available;
        private set
        {
            if (Set(ref _available, value) && !value) IsOpen = false;
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            if (value) _main.ShowDetails = false;
        }
    }

    /// <summary>Bumped after every check, so grid markers re-evaluate.</summary>
    public int Version
    {
        get => _version;
        private set => Set(ref _version, value);
    }

    public int ErrorCount => _issues.Count(i => i.Severity == HealthSeverity.Error);
    public int WarningCount => _issues.Count(i => i.Severity == HealthSeverity.Warning);
    public int SuggestionCount => _issues.Count(i => i.Severity == HealthSeverity.Suggestion);
    public int FixableCount => _issues.Count(i => i.CanFix);
    public bool IsHealthy => _issues.Count == 0;
    public bool HasFixes => FixableCount > 0;
    public bool HasIssues => _issues.Count > 0;
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => ErrorCount == 0 && WarningCount > 0;
    public bool HasOnlySuggestions => ErrorCount == 0 && WarningCount == 0 && SuggestionCount > 0;

    /// <summary>Count shown on the command bar button ("" when all is well).</summary>
    public string BadgeText => _issues.Count == 0 ? "" : _issues.Count > 99 ? "99+" : _issues.Count.ToString();

    public string Summary => _issues.Count == 0
        ? $"All checks passed on {_sheetCount} sheet{(_sheetCount == 1 ? "" : "s")}."
        : string.Join("  ·  ", new[]
            {
                Plural(ErrorCount, "error"), Plural(WarningCount, "warning"), Plural(SuggestionCount, "suggestion"),
            }.Where(s => s.Length > 0)) + $"  on {_sheetCount} sheets";

    public string FixAllText => $"Fix all ({FixableCount})";

    private static string Plural(int n, string word) => n == 0 ? "" : $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>Called when a new table is shown.</summary>
    public void Reset(bool sheetIndex)
    {
        IsAvailable = sheetIndex;
        _issues = new List<HealthIssue>();
        _byRow.Clear();
        Groups.Clear();
        RaiseAll();
    }

    /// <summary>Worst finding for a grid row (by table row index), for the status column.</summary>
    public HealthSeverity? Worst(int rowIndex) =>
        _byRow.TryGetValue(rowIndex, out var list) && list.Count > 0 ? list.Min(i => i.Severity) : null;

    public IReadOnlyList<HealthIssue> IssuesFor(int rowIndex) =>
        _byRow.TryGetValue(rowIndex, out var list) ? list : Array.Empty<HealthIssue>();

    /// <summary>Is this row a sheet (gets a status mark)?</summary>
    public bool IsSheet(int rowIndex) =>
        rowIndex >= 0 && rowIndex < _main.LoadedRows.Count && _main.LoadedRows[rowIndex].Values.ContainsKey(SheetFieldMap.NumberColumnId);

    // Many cells can change at once (paste, fill down): check once, after the burst.
    private void QueueRecheck()
    {
        if (!IsAvailable || _recheckQueued) return;
        _recheckQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _recheckQueued = false;
            Recheck();
        });
    }

    public void Recheck()
    {
        if (!IsAvailable) return;
        var rows = _main.LoadedRows;
        _sheetCount = rows.Count(r => r.Values.ContainsKey(SheetFieldMap.NumberColumnId));
        _issues = SheetHealth.Check(rows, _main.CurrentValue, Editing.Blocker);

        _byRow.Clear();
        foreach (var issue in _issues)
        {
            int i = _main.RowIndexOf(issue.Row);
            if (i < 0) continue;
            if (!_byRow.TryGetValue(i, out var list)) _byRow[i] = list = new List<HealthIssue>();
            list.Add(issue);
        }

        // Rebuild the groups, keeping each one's expanded state.
        var expanded = Groups.ToDictionary(g => g.RuleId, g => g.IsExpanded);
        Groups.Clear();
        foreach (var g in _issues.GroupBy(i => i.RuleId))
        {
            var first = g.First();
            Groups.Add(new HealthGroup(this, first.RuleId, first.RuleTitle, first.Severity, g.Select(i => new HealthItem(this, i)).ToList())
            {
                IsExpanded = expanded.TryGetValue(first.RuleId, out var open) ? open : first.Severity != HealthSeverity.Suggestion,
            });
        }
        RaiseAll();
    }

    private void RaiseAll()
    {
        Version++;
        foreach (var name in new[]
                 {
                     nameof(ErrorCount), nameof(WarningCount), nameof(HasFixes), nameof(SuggestionCount), nameof(FixableCount), nameof(IsHealthy),
                     nameof(HasIssues), nameof(HasErrors), nameof(HasWarnings), nameof(HasOnlySuggestions), nameof(BadgeText),
                     nameof(Summary), nameof(FixAllText),
                 })
            Raise(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    internal void Show(HealthIssue issue) => _main.FocusCell(issue.Row, issue.ColumnId);

    internal void Fix(HealthIssue issue) => FixAll(new[] { issue });

    internal void FixAll(IEnumerable<HealthIssue> issues)
    {
        int staged = 0;
        var problems = new List<string>();
        foreach (var issue in issues.Where(i => i.CanFix).ToList())
        {
            var reason = _main.TrySetValue(issue.Row, issue.ColumnId!, issue.FixValue!);
            if (reason is null) staged++;
            else problems.Add($"{issue.Where}: {reason}");
        }
        _main.Status = staged == 0 && problems.Count == 0 ? "Nothing to fix."
            : $"{staged} fix{(staged == 1 ? "" : "es")} added to your pending changes. Review & apply to write {(staged == 1 ? "it" : "them")} to the files."
              + (problems.Count > 0 ? $" Not fixed: {string.Join("; ", problems.Take(3))}{(problems.Count > 3 ? "…" : "")}" : "");
        Recheck();
    }
}

/// <summary>One check's findings (e.g. "Duplicate sheet numbers", 4 sheets).</summary>
public sealed class HealthGroup : Observable
{
    private readonly HealthViewModel _owner;
    private bool _isExpanded;

    public HealthGroup(HealthViewModel owner, string ruleId, string title, HealthSeverity severity, List<HealthItem> items)
    {
        _owner = owner;
        RuleId = ruleId;
        Title = title;
        Severity = severity;
        Items = items;
        FixAllCommand = new RelayCommand(() => { _owner.FixAll(Items.Select(i => i.Issue)); return Task.CompletedTask; }, () => CanFixAll);
    }

    public string RuleId { get; }
    public string Title { get; }
    public HealthSeverity Severity { get; }
    public List<HealthItem> Items { get; }
    public int Count => Items.Count;
    public bool CanFixAll => Items.Count(i => i.Issue.CanFix) > 1;
    public string FixAllText => $"Fix {Items.Count(i => i.Issue.CanFix)}";
    public RelayCommand FixAllCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }
}

public sealed class HealthItem
{
    public HealthItem(HealthViewModel owner, HealthIssue issue)
    {
        Issue = issue;
        ShowCommand = new RelayCommand(() => { owner.Show(issue); return Task.CompletedTask; });
        FixCommand = new RelayCommand(() => { owner.Fix(issue); return Task.CompletedTask; }, () => issue.CanFix);
    }

    public HealthIssue Issue { get; }
    public string Where => Issue.Where;
    public string Message => Issue.Message;
    public bool CanFix => Issue.CanFix;
    public string FixText => $"Fix → {Issue.FixValue}";
    public RelayCommand ShowCommand { get; }
    public RelayCommand FixCommand { get; }
}
