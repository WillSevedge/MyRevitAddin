using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>One drawing next to its dummy sheet, with a tick for syncing it.</summary>
public sealed class IndexRow : Observable
{
    private bool _push;
    private string _result = "";

    public IndexRow(IndexSyncRow row)
    {
        Row = row;
        _push = !row.InSync;
    }

    public IndexSyncRow Row { get; }
    public string Number => Row.Number;
    public string Drawing => $"{ExcelSources.ProgramOf(Row.Drawing)} · {Row.Drawing.Document}" + (Row.MatchedByFile ? "  (matched by file name)" : "");
    public string DrawingName => Row.DrawingName;
    public string DrawingRev => Row.DrawingRev.Length == 0 ? "–" : Row.DrawingRev;
    public string SheetKind => Row.Sheet is null ? "Missing" : Row.IsPlaceholder ? "Placeholder" : "Sheet";
    public string SheetName => Row.SheetName;
    public string SheetRev => Row.Sheet is null ? "" : Row.SheetRev.Length == 0 ? "–" : Row.SheetRev;
    public string InList => Row.InSheetList switch { true => "Yes", false => "No", _ => "" };
    public string Discipline => Row.Discipline.Length > 0 ? Row.Discipline : Row.NewDiscipline.Length > 0 ? $"({Row.NewDiscipline})" : "";
    public bool InSync => Row.InSync;
    public bool CanPush => !Row.InSync;
    public string Changes => Row.InSync ? "In sync" : string.Join(" · ", Row.Actions.Select(a => SheetIndexSync.Describe(a, Row)));

    public bool Push
    {
        get => _push;
        set
        {
            if (!CanPush) value = false;
            if (Set(ref _push, value)) Changed?.Invoke();
        }
    }

    /// <summary>What happened when synced (or why not).</summary>
    public string Result
    {
        get => _result;
        set => Set(ref _result, value);
    }

    public event Action? Changed;
}

/// <summary>
/// The sheet index window: every open DWG next to its dummy sheet in the Revit model that holds the drawing index,
/// and what the dummy sheet needs (create, make a real sheet, rename, revision, show in the sheet list, discipline).
/// Ticked rows are synced in one transaction (one Undo in Revit). Nothing is deleted.
/// </summary>
public sealed class SheetIndexViewModel : Observable
{
    public const string ToSync = "To sync", Synced = "In sync", Everything = "All";

    private readonly MainViewModel _main;
    private string _filter = ToSync;
    private string _summary = "";
    private string _message = "";
    private bool _busy;
    private List<DocumentNode> _targets = new();
    private DocumentNode? _target;
    private List<string> _disciplines = new();
    private string? _discipline;
    private bool _refreshing;

    public SheetIndexViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is IndexRow r && (_filter == Everything || (_filter == Synced ? r.InSync : !r.InSync));
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy);
        SyncCommand = new RelayCommand(SyncAsync, () => !_busy && SyncCount > 0 && _target is not null);
        TickAllCommand = new RelayCommand(() => { foreach (var r in View.Cast<IndexRow>()) r.Push = true; return Task.CompletedTask; });
        TickNoneCommand = new RelayCommand(() => { foreach (var r in View.Cast<IndexRow>()) r.Push = false; return Task.CompletedTask; });
    }

    public Window? Window { get; set; }
    public ObservableCollection<IndexRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SyncCommand { get; }
    public RelayCommand TickAllCommand { get; }
    public RelayCommand TickNoneCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            View.Refresh();
            Raise(nameof(IsToSync)); Raise(nameof(IsSynced)); Raise(nameof(IsAll)); Raise(nameof(IsEmptyView));
        }
    }

    public bool IsToSync { get => _filter == ToSync; set { if (value) Filter = ToSync; } }
    public bool IsSynced { get => _filter == Synced; set { if (value) Filter = Synced; } }
    public bool IsAll { get => _filter == Everything; set { if (value) Filter = Everything; } }
    public int CountToSync => Rows.Count(r => !r.InSync);
    public int CountSynced => Rows.Count(r => r.InSync);
    public int CountAll => Rows.Count;
    public bool IsEmptyView => !_busy && !View.Cast<object>().Any();

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(IsEmptyView));
        }
    }

    /// <summary>Open Revit models that can hold the index.</summary>
    public List<DocumentNode> Targets { get => _targets; private set => Set(ref _targets, value); }

    public DocumentNode? Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value) || value is null) return;
            _main.UiSettings.SheetIndexDocument = value.Title;
            _main.UiSettings.Save();
            if (!_refreshing) _ = RefreshAsync();
        }
    }

    /// <summary>Shared text parameters of the index model's sheets (the one the index schedules sort by).</summary>
    public List<string> Disciplines { get => _disciplines; private set => Set(ref _disciplines, value); }

    public string? Discipline
    {
        get => _discipline;
        set
        {
            if (!Set(ref _discipline, value)) return;
            _main.UiSettings.SheetIndexDiscipline = value;
            _main.UiSettings.Save();
            if (!_refreshing) _ = RefreshAsync();
        }
    }

    public bool NoTitleBlock
    {
        get => _main.UiSettings.SheetIndexNoTitleBlock;
        set
        {
            _main.UiSettings.SheetIndexNoTitleBlock = value;
            _main.UiSettings.Save();
            Raise();
        }
    }

    public int SyncCount => Rows.Count(r => r.Push);
    public string SyncText => SyncCount == 0 ? "Sync" : $"Sync {SyncCount} dummy sheet{(SyncCount == 1 ? "" : "s")}";

    private void OnPushChanged()
    {
        Raise(nameof(SyncCount));
        Raise(nameof(SyncText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Lines the DWGs up with the index model's sheets, as loaded in the main window's Sheets view.</summary>
    public Task RefreshAsync()
    {
        _refreshing = true;
        try
        {
            Targets = _main.IndexTargets();
            if (_target is null || !_targets.Contains(_target))
            {
                string? remembered = _main.UiSettings.SheetIndexDocument;
                // The model with the G sheets (the index) is usually the architectural one; else the one remembered, else the first.
                Target = _targets.FirstOrDefault(d => string.Equals(d.Title, remembered, StringComparison.OrdinalIgnoreCase))
                         ?? _targets.FirstOrDefault(d => d.Title.Contains("arch", StringComparison.OrdinalIgnoreCase))
                         ?? _targets.FirstOrDefault();
            }

            Rows.Clear();
            var table = _main.Table;
            if (_target is null)
            {
                Summary = "";
                Message = "Open the Revit model that holds the drawing index (with Nexus), then Refresh.";
                return Task.CompletedTask;
            }
            if (table is null || !_main.IsSheetsView)
            {
                Message = "Open Sheets & data › Sheets in the main window, tick the index model and the DWGs, then Refresh.";
                return Task.CompletedTask;
            }
            if (!SheetIndexSync.IndexSheets(table, _target.Title).Any())
            {
                Message = $"{_target.Title} is not ticked in the main window: tick it under Sources (with the DWGs), then Refresh.";
                return Task.CompletedTask;
            }

            Disciplines = SheetIndexSync.DisciplineParameters(table, _target.Title);
            if (_discipline is null || !_disciplines.Contains(_discipline, StringComparer.OrdinalIgnoreCase))
            {
                string? remembered = _main.UiSettings.SheetIndexDiscipline;
                Discipline = _disciplines.FirstOrDefault(d => string.Equals(d, remembered, StringComparison.OrdinalIgnoreCase))
                             ?? _disciplines.FirstOrDefault(d => d.Contains("discipline", StringComparison.OrdinalIgnoreCase))
                             ?? _disciplines.FirstOrDefault();
            }

            foreach (var r in SheetIndexSync.Plan(table, _target.Title, _discipline))
            {
                var row = new IndexRow(r);
                row.Changed += OnPushChanged;
                Rows.Add(row);
            }
            int drawings = table.Rows.Count(r => !SheetIndexSync.IsRevit(r) && r.Depth == 0);
            Summary = $"{drawings} DWG sheet{(drawings == 1 ? "" : "s")} open · {Rows.Count} matched or missing in {_target.Title}";
            Message = drawings == 0
                ? "No DWG is ticked in the main window: tick the drawings under Sources, then Refresh."
                : CountToSync == 0 ? "Every dummy sheet matches its drawing." : "";
        }
        finally
        {
            _refreshing = false;
            foreach (var name in new[] { nameof(CountToSync), nameof(CountSynced), nameof(CountAll), nameof(IsEmptyView) }) Raise(name);
            View.Refresh();
            OnPushChanged();
        }
        return Task.CompletedTask;
    }

    private async Task SyncAsync()
    {
        var target = _target;
        var rows = Rows.Where(r => r.Push).ToList();
        if (target is null || rows.Count == 0) return;
        int create = rows.Count(r => r.Row.Actions.Contains(IndexAction.Create));
        int convert = rows.Count(r => r.Row.Actions.Contains(IndexAction.MakeSheet));
        var lines = new List<string>();
        if (create > 0) lines.Add($"{create} new dummy sheet(s){(NoTitleBlock ? " without a title block" : "")}");
        if (convert > 0) lines.Add($"{convert} placeholder(s) made real sheets (same number, name and parameters) so they can show revisions");
        if (rows.Count - create > 0) lines.Add($"{rows.Count - create} existing sheet(s) updated (name, revision, sheet list, discipline)");
        if (!Dialogs.Confirm($"Sync {rows.Count} dummy sheet(s) in {target.Title}?",
                string.Join("\n", lines.Select(l => "• " + l)) + "\n\nNothing is deleted. One Undo in Revit reverses all of it.",
                "Sync", kind: NoticeKind.Info, owner: Window))
            return;

        var request = new CreateSheetsRequest
        {
            DocumentId = target.Info.Id,
            NoTitleBlock = NoTitleBlock,
            Sheets = rows.Select(r => SheetIndexSync.ToRequest(r.Row, _discipline)).ToList(),
        };
        IsBusy = true;
        try
        {
            var result = await _main.SyncIndexAsync(target, request);
            if (result is null)
            {
                Message = "Nothing changed.";
                return;
            }
            var outcome = result.Sheets.ToDictionary(s => s.Number, StringComparer.OrdinalIgnoreCase);
            var problems = result.Sheets.Where(s => s.Message is not null).Select(s => $"{s.Number}: {s.Message}").ToList();
            int created = result.Sheets.Count(s => s.Created), updated = result.Sheets.Count(s => s.Updated);
            await RefreshAsync();
            // Keep what happened beside each drawing (also for rows now in sync).
            foreach (var row in Rows)
                if (outcome.TryGetValue(row.Number, out var o))
                    row.Result = o.Created ? "Created" + (o.Message is null ? "" : ". " + o.Message)
                        : o.Updated ? string.Join(", ", o.Changes) + (o.Message is null ? "" : ". " + o.Message)
                        : o.Message ?? "No change";
            Message = $"{created} created, {updated} updated in {result.DocumentTitle}" +
                      (result.UndoName is null ? "." : $" (Undo in Revit: \"{result.UndoName}\").") +
                      (problems.Count > 0 ? $" {problems.Count} note(s): " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? "…" : "") : "");
        }
        catch (AgentRequestException ex)
        {
            Message = "Nothing was synced: " + ex.Error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
