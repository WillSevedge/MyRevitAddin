using System.Collections.ObjectModel;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

/// <summary>One pending edit in the review, whether it is included, and what happened when it was applied.</summary>
public sealed class ChangeRow : Observable
{
    private string _status = "";
    private string? _message;
    private string? _result;
    private bool _include = true;

    public ChangeRow(CellEdit edit) => Edit = edit;

    public CellEdit Edit { get; }
    public string Document => Edit.Row.Document;
    public string Item => Edit.Row.Item.Trim();
    /// <summary>
    /// The property's name: a standard sheet field's one name (Sheet Number, Sheet Name) when the edited
    /// property is one, whatever the program calls it; otherwise the program's name.
    /// </summary>
    public string Property
    {
        get
        {
            foreach (var (columnId, value) in Edit.Row.Values)
                if (ReferenceEquals(value, Edit.Property) && columnId.StartsWith(SheetFieldMap.Group + " ›", StringComparison.Ordinal))
                    return SheetFieldMap.DisplayName(columnId[(SheetFieldMap.Group.Length + 2)..].Trim());
            return Edit.Property.Name;
        }
    }
    public string OldValue => Edit.OldValue;
    public string NewValue => Edit.NewValue;
    public bool OldIsEmpty => OldValue.Length == 0;
    public bool NewIsEmpty => NewValue.Length == 0;

    /// <summary>Raised when the checkbox changes (groups update their tri-state box and the counts).</summary>
    public event Action? IncludeChanged;

    public bool Include
    {
        get => _include;
        set
        {
            if (IsDone) return;
            if (Set(ref _include, value)) IncludeChanged?.Invoke();
        }
    }

    /// <summary>"" before applying, then Applied / Unchanged / Skipped / Failed.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsDone));
            Raise(nameof(StatusText));
        }
    }

    public bool IsDone => _status.Length > 0;

    public string StatusText => _status switch
    {
        "Applied" => "Applied",
        "Unchanged" => "Already set",
        "Skipped" => "Skipped",
        "Failed" => "Failed",
        "NotSent" => "Not applied (unticked)",
        _ => "",
    };

    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    /// <summary>The value as the host shows it after applying.</summary>
    public string? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public void SetOutcome(string status, string? message, string? result)
    {
        Status = status;
        Message = message;
        Result = result;
    }
}

/// <summary>A tri-state checkbox over a set of changes.</summary>
public abstract class ChangeGroup : Observable
{
    public abstract IEnumerable<ChangeRow> All { get; }

    /// <summary>All ticked: true; none: false; some: null (shown as a dash).</summary>
    public bool? IsChecked
    {
        get
        {
            int on = All.Count(r => r.Include);
            return on == 0 ? false : on == Count ? true : null;
        }
        set
        {
            bool include = value != false;
            foreach (var r in All) r.Include = include;
        }
    }

    public int Count => All.Count();
    public int IncludedCount => All.Count(r => r.Include);

    internal void Refresh()
    {
        Raise(nameof(IsChecked));
        Raise(nameof(IncludedCount));
    }
}

/// <summary>One sheet (or other item) and its changes.</summary>
public sealed class ChangeItemGroup : ChangeGroup
{
    public ChangeItemGroup(string title, List<ChangeRow> changes)
    {
        Title = title;
        Changes = changes;
    }

    public string Title { get; }
    public List<ChangeRow> Changes { get; }
    public override IEnumerable<ChangeRow> All => Changes;
}

/// <summary>One file (model or drawing) and its sheets.</summary>
public sealed class ChangeFileGroup : ChangeGroup
{
    public ChangeFileGroup(string name, string program, List<ChangeItemGroup> items)
    {
        Name = name;
        Program = program;
        Items = items;
    }

    public string Name { get; }
    public string Program { get; }
    public List<ChangeItemGroup> Items { get; }
    public override IEnumerable<ChangeRow> All => Items.SelectMany(i => i.Changes);
    public string CountText => $"{Count} change{(Count == 1 ? "" : "s")}";
}

/// <summary>
/// Review &amp; apply: every pending edit grouped by file and sheet, old value → new value, with a checkbox per
/// change (and per sheet and file). Applies the ticked changes and shows each outcome in place.
/// </summary>
public sealed class ApplyChangesViewModel : Observable
{
    private readonly Func<IReadOnlyList<ChangeRow>, Task<List<ResultRun>>> _apply;
    private bool _applying;
    private bool _applied;
    private string _resultSummary = "";

    public ApplyChangesViewModel(IEnumerable<CellEdit> edits, Func<IReadOnlyList<ChangeRow>, Task<List<ResultRun>>> apply)
    {
        _apply = apply;
        // A sheet field and the parameter it shows are one property: list it once.
        foreach (var e in edits.GroupBy(e => (e.ProcessId, e.DocumentId, e.ToChange().OwnerId, e.Property.Id ?? e.Property.Name)).Select(g => g.Last()))
        {
            var row = new ChangeRow(e);
            row.IncludeChanged += OnIncludeChanged;
            Changes.Add(row);
        }
        foreach (var file in Changes.GroupBy(c => (c.Edit.ProcessId, c.Edit.DocumentId)))
        {
            var items = file.GroupBy(c => c.Edit.Row.ItemId)
                .Select(g => new ChangeItemGroup(g.First().Item, g.ToList()))
                .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var first = file.First().Edit.Row;
            Files.Add(new ChangeFileGroup(first.Document, first.Source.Host.Name, items));
        }

        ApplyCommand = new RelayCommand(ApplyAsync, () => !_applying && !_applied && IncludedCount > 0);
        SelectAllCommand = new RelayCommand(() => { SetAll(true); return Task.CompletedTask; }, () => !_applied);
        SelectNoneCommand = new RelayCommand(() => { SetAll(false); return Task.CompletedTask; }, () => !_applied);
    }

    public ObservableCollection<ChangeRow> Changes { get; } = new();
    public ObservableCollection<ChangeFileGroup> Files { get; } = new();
    public RelayCommand ApplyCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    /// <summary>Results whose documents were changed and should be read again.</summary>
    public List<ResultRun> ChangedRuns { get; private set; } = new();

    /// <summary>Changes left unticked: they stay pending in the table.</summary>
    public IReadOnlyList<ChangeRow> NotApplied => Changes.Where(c => !c.Include).ToList();

    public int IncludedCount => Changes.Count(c => c.Include);
    public int FileCount => Changes.Where(c => c.Include).Select(c => (c.Edit.ProcessId, c.Edit.DocumentId)).Distinct().Count();

    /// <summary>"Apply 42 changes to 3 files", then the outcome.</summary>
    public string Summary => _applied ? _resultSummary
        : IncludedCount == 0 ? "Nothing selected"
        : $"Apply {IncludedCount} change{(IncludedCount == 1 ? "" : "s")} to {FileCount} file{(FileCount == 1 ? "" : "s")}";

    public string Subtitle => _applied
        ? "Results are shown next to each change."
        : $"{Changes.Count} pending change{(Changes.Count == 1 ? "" : "s")}. Untick any you want to keep for later; they stay in the table.";

    public string ApplyText => _applying ? "Applying…" : IncludedCount == 0 ? "Apply" : $"Apply {IncludedCount} change{(IncludedCount == 1 ? "" : "s")}";

    public bool Applied
    {
        get => _applied;
        private set
        {
            if (!Set(ref _applied, value)) return;
            Raise(nameof(CloseLabel));
            Raise(nameof(Summary));
            Raise(nameof(Subtitle));
        }
    }

    /// <summary>True while changes are being sent; the window cannot close meanwhile.</summary>
    public bool IsApplying => _applying;

    public string CloseLabel => _applied ? "Close" : "Cancel";

    /// <summary>Raised after applying when every ticked change went through, so the window can close itself.</summary>
    public event Action? Succeeded;

    private void SetAll(bool include)
    {
        foreach (var c in Changes) c.Include = include;
    }

    private void OnIncludeChanged()
    {
        foreach (var f in Files)
        {
            f.Refresh();
            foreach (var i in f.Items) i.Refresh();
        }
        Raise(nameof(IncludedCount));
        Raise(nameof(FileCount));
        Raise(nameof(Summary));
        Raise(nameof(ApplyText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplyAsync()
    {
        _applying = true;
        Raise(nameof(ApplyText));
        var included = Changes.Where(c => c.Include).ToList();
        try
        {
            ChangedRuns = await _apply(included);
        }
        finally
        {
            _applying = false;
            foreach (var c in Changes.Where(c => !c.Include)) c.SetOutcome("NotSent", null, null);
            var counts = included.GroupBy(c => c.Status).Select(g => $"{g.Count()} {Word(g.Key)}");
            _resultSummary = "Done: " + string.Join(", ", counts);
            Applied = true;
            Raise(nameof(ApplyText));
            foreach (var f in Files)
            {
                f.Refresh();
                foreach (var i in f.Items) i.Refresh();
            }
        }

        if (included.All(c => c.Status is "Applied" or "Unchanged")) Succeeded?.Invoke();
    }

    private static string Word(string status) => status switch
    {
        "Applied" => "applied",
        "Unchanged" => "already set",
        "Skipped" => "skipped",
        "Failed" => "failed",
        _ => status.ToLowerInvariant(),
    };
}
