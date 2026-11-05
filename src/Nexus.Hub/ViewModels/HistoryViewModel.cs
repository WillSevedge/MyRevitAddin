using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Core.History;

namespace Nexus.Hub.ViewModels;

/// <summary>A saved snapshot in the list.</summary>
public sealed class SnapshotItem
{
    public SnapshotItem(SheetSnapshot snapshot) => Snapshot = snapshot;

    public SheetSnapshot Snapshot { get; }
    public string Title => Snapshot.Name;
    public string When => Snapshot.CreatedUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public bool IsAutomatic => Snapshot.Kind == SheetSnapshot.KindBeforeApply;
    public string Detail => $"{Snapshot.Sheets.Count} sheets · {Snapshot.Files.Count} file{(Snapshot.Files.Count == 1 ? "" : "s")}"
                            + (IsAutomatic ? " · automatic" : "") + (Snapshot.User.Length > 0 ? $" · {Snapshot.User}" : "");
}

/// <summary>One sheet's difference, for the list.</summary>
public sealed class SheetDiffItem
{
    public SheetDiffItem(SheetDiff diff) => Diff = diff;

    public SheetDiff Diff { get; }
    public string Kind => Diff.Kind.ToString();
    public string KindText => Diff.Kind switch
    {
        SheetDiffKind.Added => "New",
        SheetDiffKind.Removed => "Removed",
        _ => "Changed",
    };
    public string Sheet => Diff.Title.Length > 0 ? $"{Diff.Number} - {Diff.Title}" : Diff.Number;
    public string File => Diff.File;
    public IReadOnlyList<FieldDiff> Fields => Diff.Fields;
}

/// <summary>
/// History: snapshots of the sheet set (taken by hand, and automatically before every apply), what changed
/// since any of them, and the log of every change Nexus applied.
/// </summary>
public sealed class HistoryViewModel : Observable
{
    private readonly MainViewModel _main;
    private SnapshotItem? _selected;
    private string _newName;
    private string _compareSummary = "";
    private string _compareNote = "";
    private string _logSearch = "";
    private List<ChangeLogEntry> _log = new();
    private SnapshotComparison? _comparison;

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        _newName = DefaultName();
        TakeSnapshotCommand = new RelayCommand(() => { TakeSnapshot(); return Task.CompletedTask; }, () => _main.IsSheetsView);
        DeleteCommand = new RelayCommand(() => { Delete(); return Task.CompletedTask; }, () => _selected is not null);
        ExportCommand = new RelayCommand(() => { Export(); return Task.CompletedTask; }, () => _comparison is { Sheets.Count: > 0 });
        Reload();
        _log = ChangeLog.Read();
        FilterLog();
    }

    public ObservableCollection<SnapshotItem> Snapshots { get; } = new();
    public ObservableCollection<SheetDiffItem> Differences { get; } = new();
    public ObservableCollection<ChangeLogEntry> LogEntries { get; } = new();
    public RelayCommand TakeSnapshotCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }

    public bool CanSnapshot => _main.IsSheetsView;
    public string SnapshotHint => _main.IsSheetsView
        ? "Saves every sheet of the open files as they are now. Nexus also saves one automatically before every apply."
        : "Open the Sheets view to take or compare snapshots.";

    public string NewName
    {
        get => _newName;
        set => Set(ref _newName, value ?? "");
    }

    public SnapshotItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) Compare();
        }
    }

    public string CompareSummary
    {
        get => _compareSummary;
        private set => Set(ref _compareSummary, value);
    }

    public string CompareNote
    {
        get => _compareNote;
        private set => Set(ref _compareNote, value);
    }

    public bool HasNoDifferences => _comparison is { Sheets.Count: 0 };

    public string LogSearch
    {
        get => _logSearch;
        set
        {
            if (Set(ref _logSearch, value ?? "")) FilterLog();
        }
    }

    public string LogSummary => _log.Count == 0
        ? "No changes applied with Nexus yet."
        : $"{LogEntries.Count} of {_log.Count} change{(_log.Count == 1 ? "" : "s")} applied with Nexus (newest first).";

    private static string DefaultName() => "Snapshot " + DateTime.Now.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    private void Reload(string? select = null)
    {
        Snapshots.Clear();
        foreach (var s in SnapshotStore.List()) Snapshots.Add(new SnapshotItem(s));
        Selected = Snapshots.FirstOrDefault(s => s.Snapshot.Id == select) ?? Snapshots.FirstOrDefault();
        if (Snapshots.Count == 0) Compare();
    }

    private void TakeSnapshot()
    {
        var snap = _main.CaptureSnapshot(NewName.Trim().Length > 0 ? NewName.Trim() : DefaultName(), SheetSnapshot.KindManual);
        if (snap is null) return;
        SnapshotStore.Save(snap);
        _main.Status = $"Snapshot \"{snap.Name}\" saved ({snap.Sheets.Count} sheets).";
        NewName = DefaultName();
        Reload(snap.Id);
    }

    private void Delete()
    {
        if (_selected is null) return;
        SnapshotStore.Delete(_selected.Snapshot.Id);
        Reload();
    }

    private void Compare()
    {
        Differences.Clear();
        _comparison = null;
        if (_selected is null)
        {
            CompareSummary = "No snapshots yet";
            CompareNote = "Take a snapshot now; later you can see exactly what changed since.";
        }
        else if (_main.CaptureSnapshot("now", "now") is not { } now)
        {
            CompareSummary = "Open the Sheets view to compare";
            CompareNote = "The comparison uses the sheets loaded in the Sheets view.";
        }
        else
        {
            _comparison = SnapshotComparison.Compare(_selected.Snapshot, now);
            foreach (var d in _comparison.Sheets) Differences.Add(new SheetDiffItem(d));
            CompareSummary = _comparison.Sheets.Count == 0
                ? $"Nothing changed since {_selected.When}"
                : string.Join("  ·  ", new[]
                {
                    _comparison.Changed > 0 ? $"{_comparison.Changed} changed" : "",
                    _comparison.Added > 0 ? $"{_comparison.Added} new" : "",
                    _comparison.Removed > 0 ? $"{_comparison.Removed} removed" : "",
                }.Where(s => s.Length > 0)) + $"  since {_selected.When}";
            var notes = new List<string>();
            if (_comparison.FilesNotOpen.Count > 0) notes.Add("Not open now, so not compared: " + string.Join(", ", _comparison.FilesNotOpen));
            if (_comparison.FilesNew.Count > 0) notes.Add("Not in the snapshot: " + string.Join(", ", _comparison.FilesNew));
            CompareNote = string.Join(".  ", notes);
        }
        Raise(nameof(HasNoDifferences));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The comparison as a workbook: one row per changed value (and per new or removed sheet).</summary>
    private void Export()
    {
        if (_comparison is null || _selected is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export the changes",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Sheet changes since {_selected.Snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HHmm}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var d in _comparison.Sheets)
        {
            if (d.Fields.Count == 0) rows.Add(new[] { d.File, d.Number, d.Title, d.Kind.ToString(), "", "", "" });
            foreach (var f in d.Fields) rows.Add(new[] { d.File, d.Number, d.Title, "Changed", f.Field, f.Before, f.After });
        }
        try
        {
            ExcelWorkbook.Export(dialog.FileName, new[] { "File", "Sheet", "Title", "Change", "Field", "Before", "After" }, rows, "Changes");
            _main.Status = $"Exported {rows.Count} row(s) to {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            _main.Status = "Could not export: " + ex.Message;
        }
    }

    private void FilterLog()
    {
        LogEntries.Clear();
        string q = _logSearch.Trim();
        foreach (var e in _log.Where(e => q.Length == 0
                     || $"{e.File} {e.Item} {e.Property} {e.Before} {e.After} {e.User} {e.Program}".Contains(q, StringComparison.OrdinalIgnoreCase)).Take(2000))
            LogEntries.Add(e);
        Raise(nameof(LogSummary));
    }
}
