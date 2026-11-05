using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// One field of one drawing: the list's value, the model's, and whether the list's value is pushed into the model.
/// </summary>
public sealed class FieldCompare : Observable
{
    private bool _push;

    public FieldCompare(string field, string columnId, string list, string model, bool differs, string? blocker, bool hasModel)
    {
        Field = field;
        ColumnId = columnId;
        ListValue = list;
        ModelValue = model;
        Differs = differs;
        // Only a list value can be pushed (an empty list cell does not clear the model).
        Blocker = !differs ? null : list.Trim().Length == 0 ? "The list has no value here" : blocker;
        HasModel = hasModel;
        _push = CanPush;
    }

    public string Field { get; }
    public string ColumnId { get; }
    public string ListValue { get; }
    public string ModelValue { get; }
    public bool Differs { get; }
    public bool HasModel { get; }
    /// <summary>Why the model's value cannot be changed from here (e.g. Revit revisions), or null.</summary>
    public string? Blocker { get; }

    public bool CanPush => Differs && Blocker is null;
    public bool IsLocked => Differs && Blocker is not null;

    /// <summary>Shown in the cell: the list's value, or the model's for sheets that are not on the list.</summary>
    public string Display => ListValue.Length > 0 || HasModel is false ? ListValue : ModelValue;
    /// <summary>Under it, when they differ: what the model has now.</summary>
    public string ModelText => ModelValue.Trim().Length == 0 ? "model: (empty)" : "model: " + ModelValue;

    public string ToolTip => !Differs ? (Display.Length > 0 ? Display : "(empty)")
        : $"List: {(ListValue.Length > 0 ? ListValue : "(empty)")}\nModel: {(ModelValue.Length > 0 ? ModelValue : "(empty)")}"
          + (Blocker is null ? "\nTicked: the list's value goes into the model." : "\nNot pushed: " + Blocker);

    public bool Push
    {
        get => _push;
        set
        {
            if (!CanPush) value = false;
            if (Set(ref _push, value)) Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

/// <summary>One drawing number: the list's values next to the model's, and what is pushed into the model.</summary>
public sealed class DeliverableRow : Observable
{
    private bool _create;

    public DeliverableRow(DeliverableComparison c, Func<TableRow?, string, string?> blocker)
    {
        C = c;
        FieldCompare F(string field, string columnId, string list, string model, bool differs) =>
            new(field, columnId, list, model, differs, blocker(c.Model, columnId), c.Model is not null);
        Title = F("Sheet Name", DeliverablesCompare.TitleId, c.List?.Title ?? "", c.ModelTitle, c.TitleDiffers);
        Rev = F("Rev", DeliverablesCompare.RevId, c.List?.Rev ?? "", c.ModelRev, c.RevDiffers);
        Date = F("Date", DeliverablesCompare.DateId, c.List?.Date ?? "", c.ModelDate, c.DateDiffers);
        Status = F("Status", DeliverablesCompare.StatusId, c.List?.Status ?? "", c.ModelStatus, c.StatusDiffers);
        foreach (var f in Fields) f.Changed += () => { Raise(nameof(Push)); Changed?.Invoke(); };
        _create = IsNew;
    }

    public DeliverableComparison C { get; }
    public FieldCompare Title { get; }
    public FieldCompare Rev { get; }
    public FieldCompare Date { get; }
    public FieldCompare Status { get; }
    public IEnumerable<FieldCompare> Fields => new[] { Title, Rev, Date, Status };

    public string Number => C.Number;
    public string Discipline => C.List?.Discipline ?? "";
    public string Source => C.Model is null ? "" : $"{C.Program} · {C.File}" + (C.MatchedByFile ? "  (matched by file name)" : "");
    public DeliverableResult Result => C.Result;
    public bool IsNew => C.Result == DeliverableResult.NotInModel;
    /// <summary>On the list and in the model with a value that can be pushed, or a new sheet.</summary>
    public bool CanPush => IsNew || Fields.Any(f => f.CanPush);

    public string ResultText => C.Result switch
    {
        DeliverableResult.Match => "Match",
        DeliverableResult.Differs => "Differs",
        DeliverableResult.NotInModel => "New sheet",
        _ => "Not on list",
    };

    public string ResultToolTip => C.Result switch
    {
        DeliverableResult.Match => "The model matches the list.",
        DeliverableResult.Differs => "The model has other values than the list: ticked values go from the list into the model.",
        DeliverableResult.NotInModel => "On the list, not in the model: ticked, a sheet is created with the list's number, name and status.",
        _ => "In the model, not on the list. The list is the source of truth: add it to the list in Excel, or leave it.",
    };

    /// <summary>Row tick: a new sheet is created, or every pushable value goes into the model.</summary>
    public bool Push
    {
        get => IsNew ? _create : Fields.Any(f => f.Push);
        set
        {
            if (IsNew)
            {
                if (Set(ref _create, value)) Changed?.Invoke();
                return;
            }
            foreach (var f in Fields) f.Push = value;
            Raise();
        }
    }

    public event Action? Changed;
}

/// <summary>
/// The workbook's deliverables list next to what Nexus reads from the open models (Revit, AutoCAD, Civil 3D,
/// Plant 3D). The list is the source of truth and is never written: ticked values are pushed from the list into the
/// models (and drawings the models lack become new sheets), with a review before anything is written. Nexus only
/// writes the workbook's Model Export table.
/// </summary>
public sealed class DeliverablesViewModel : Observable
{
    public const string NeedsPush = "To push", Everything = "All", Differs = "Differs", New = "New sheets", NotOnList = "Not on list", Matching = "Match";

    private readonly MainViewModel _main;
    private DeliverablesWorkbook? _workbook;
    private string _filter = NeedsPush;
    private string _summary = "";
    private string _message = "";
    private string _readAt = "";
    private bool _busy;
    private int _version;
    private List<DocumentNode> _targets = new();
    private DocumentNode? _target;

    public DeliverablesViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is DeliverableRow r && Shows(r);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy);
        ChangeWorkbookCommand = new RelayCommand(async () =>
        {
            if (_main.ChooseWorkbook()) await RefreshAsync();
        }, () => !_busy);
        ExcelSelectionCommand = new RelayCommand(UseExcelSelectionAsync, () => !_busy && Rows.Count > 0);
        PushCommand = new RelayCommand(PushAsync, () => !_busy && PushCount > 0);
        TickAllCommand = new RelayCommand(() => { SetPush(VisibleRows(), true); return Task.CompletedTask; });
        TickNoneCommand = new RelayCommand(() => { SetPush(VisibleRows(), false); return Task.CompletedTask; });
        ExportCommand = new RelayCommand(async () =>
        {
            if (await _main.ExportModelAsync()) Message = _main.Status;
        }, () => !_busy);
    }

    public Window? Window { get; set; }

    public ObservableCollection<DeliverableRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ChangeWorkbookCommand { get; }
    public RelayCommand ExcelSelectionCommand { get; }
    public RelayCommand PushCommand { get; }
    public RelayCommand TickAllCommand { get; }
    public RelayCommand TickNoneCommand { get; }
    public RelayCommand ExportCommand { get; }

    public string Title => "Deliverables ⇄ Models — " + _main.ExcelLinkTitle;
    public string WorkbookName => _main.ExcelLinkTitle;
    public string WorkbookPath => _main.WorkbookPath ?? "";
    /// <summary>"List read from 'Deliverables List' as last saved · 10:42".</summary>
    public string ReadAt { get => _readAt; private set => Set(ref _readAt, value); }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            View.Refresh();
            Raise(nameof(IsNeedsPush)); Raise(nameof(IsAll)); Raise(nameof(IsDiffers)); Raise(nameof(IsNew)); Raise(nameof(IsNotOnList)); Raise(nameof(IsMatch));
            Raise(nameof(IsEmptyView));
        }
    }

    // Filter chips (radio buttons).
    public bool IsNeedsPush { get => _filter == NeedsPush; set { if (value) Filter = NeedsPush; } }
    public bool IsAll { get => _filter == Everything; set { if (value) Filter = Everything; } }
    public bool IsDiffers { get => _filter == Differs; set { if (value) Filter = Differs; } }
    public bool IsNew { get => _filter == New; set { if (value) Filter = New; } }
    public bool IsNotOnList { get => _filter == NotOnList; set { if (value) Filter = NotOnList; } }
    public bool IsMatch { get => _filter == Matching; set { if (value) Filter = Matching; } }

    public int CountNeedsPush => Rows.Count(r => r.Result is DeliverableResult.Differs or DeliverableResult.NotInModel);
    public int CountAll => Rows.Count;
    public int CountDiffers => Rows.Count(r => r.Result == DeliverableResult.Differs);
    public int CountNew => Rows.Count(r => r.Result == DeliverableResult.NotInModel);
    public int CountNotOnList => Rows.Count(r => r.Result == DeliverableResult.NotOnList);
    public int CountMatch => Rows.Count(r => r.Result == DeliverableResult.Match);
    public bool IsEmptyView => !_busy && Rows.Count > 0 && !View.Cast<object>().Any();
    public bool HasRows => Rows.Count > 0;

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(IsEmptyView));
        }
    }

    /// <summary>Open Revit models new sheets can go in.</summary>
    public List<DocumentNode> Targets
    {
        get => _targets;
        private set
        {
            Set(ref _targets, value);
            Raise(nameof(HasTargets));
        }
    }

    public bool HasTargets => _targets.Count > 0;

    public DocumentNode? Target
    {
        get => _target;
        set => Set(ref _target, value);
    }

    /// <summary>What Push to models will do.</summary>
    public int PushValues => Rows.Where(r => !r.IsNew).Sum(r => r.Fields.Count(f => f.Push));
    public int PushSheets => Rows.Count(r => !r.IsNew && r.Fields.Any(f => f.Push));
    public int PushNew => Rows.Count(r => r.IsNew && r.Push);
    public int PushCount => PushValues + PushNew;
    public bool HasNewToPush => PushNew > 0;

    public string PushSummary
    {
        get
        {
            if (PushCount == 0) return Rows.Count == 0 ? "" : "Nothing ticked to push.";
            var parts = new List<string>();
            if (PushValues > 0) parts.Add($"{PushValues} value{(PushValues == 1 ? "" : "s")} on {PushSheets} sheet{(PushSheets == 1 ? "" : "s")}");
            if (PushNew > 0) parts.Add($"{PushNew} new sheet{(PushNew == 1 ? "" : "s")}");
            return "From the list into the models: " + string.Join(" · ", parts);
        }
    }

    public string PushText => PushCount == 0 ? "Push to models" : $"Push {PushCount} to models";

    private void OnPushChanged()
    {
        Raise(nameof(PushValues)); Raise(nameof(PushSheets)); Raise(nameof(PushNew)); Raise(nameof(PushCount));
        Raise(nameof(HasNewToPush)); Raise(nameof(PushSummary)); Raise(nameof(PushText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private bool Shows(DeliverableRow r) => _filter switch
    {
        Everything => true,
        Differs => r.Result == DeliverableResult.Differs,
        New => r.Result == DeliverableResult.NotInModel,
        NotOnList => r.Result == DeliverableResult.NotOnList,
        Matching => r.Result == DeliverableResult.Match,
        _ => r.Result is DeliverableResult.Differs or DeliverableResult.NotInModel,
    };

    private List<DeliverableRow> VisibleRows() => View.Cast<DeliverableRow>().ToList();

    private static void SetPush(IEnumerable<DeliverableRow> rows, bool push)
    {
        foreach (var r in rows) r.Push = push;
    }

    /// <summary>Reads the workbook (as last saved) and compares it with the loaded sheets.</summary>
    public async Task RefreshAsync()
    {
        Raise(nameof(Title));
        Raise(nameof(WorkbookName));
        Raise(nameof(WorkbookPath));
        Targets = _main.SheetTargets();
        if (_target is null || !_targets.Contains(_target))
            Target = _targets.FirstOrDefault(d => d.IsChecked) ?? _targets.FirstOrDefault();
        if (_main.WorkbookPath is not { } path) return;
        var table = _main.Table;
        if (table is null || !_main.IsSheetsView)
        {
            Message = "Open Sheets & data › Sheets in the main window and tick the models, then Refresh.";
            return;
        }
        int version = ++_version;
        IsBusy = true;
        Message = $"Reading {Path.GetFileName(path)}…";
        try
        {
            var (info, comparison) = await Task.Run(() =>
            {
                var wb = Deliverables.Read(path);
                return (wb, DeliverablesCompare.Compare(wb.Items, table));
            });
            if (version != _version) return;
            _workbook = info;
            Rows.Clear();
            foreach (var c in comparison
                         .OrderBy(c => c.Result switch { DeliverableResult.NotInModel => 0, DeliverableResult.Differs => 1, DeliverableResult.NotOnList => 3, _ => 2 })
                         .ThenBy(c => c.Number, NaturalComparer.Instance))
            {
                var row = new DeliverableRow(c, (model, columnId) => model is null ? null : Editing.Blocker(model, columnId));
                row.Changed += OnPushChanged;
                Rows.Add(row);
            }
            foreach (var name in new[] { nameof(CountNeedsPush), nameof(CountAll), nameof(CountDiffers), nameof(CountNew), nameof(CountNotOnList), nameof(CountMatch), nameof(HasRows) })
                Raise(name);
            OnPushChanged();

            int files = table.Rows.Select(r => r.Document).Distinct().Count();
            Summary = $"{info.Items.Count} drawing{(info.Items.Count == 1 ? "" : "s")} on the list · {table.Rows.Count} sheet{(table.Rows.Count == 1 ? "" : "s")} in " +
                      $"{files} open file{(files == 1 ? "" : "s")}";
            ReadAt = info.ListSheet.Length > 0 ? $"List read from '{info.ListSheet}' as last saved · {DateTime.Now:t}" : $"Read {DateTime.Now:t}";
            var notes = new List<string>(info.Warnings);
            if (info.Items.Count == 0)
                // Nothing numbered: say where the numbers come from rather than showing every sheet as "not on list".
                notes.Add($"The list has no drawing numbers yet{(info.Unnumbered > 0 ? $" ({info.Unnumbered} item(s) without one)" : "")}, so nothing can be " +
                          "matched. Enter the drawings (Dwg. No. and Drawing Title) in the discipline tabs' Drawings section, save the " +
                          "workbook, then Refresh. Drawings are matched by number.");
            else if (info.Unnumbered > 0) notes.Add($"{info.Unnumbered} item(s) on the list have no drawing number yet and are not compared.");
            int locked = Rows.Sum(r => r.Fields.Count(f => f.IsLocked));
            if (locked > 0) notes.Add($"{locked} difference(s) can't be pushed (marked with a lock: hover for why).");
            Message = string.Join(" ", notes);
        }
        catch (Exception ex)
        {
            if (version == _version) Message = "Could not read the workbook: " + ex.Message;
        }
        finally
        {
            if (version == _version) IsBusy = false;
            View.Refresh();
            Raise(nameof(IsEmptyView));
        }
    }

    /// <summary>Ticks only the drawings selected in Excel (rows of the list, or cells holding drawing numbers).</summary>
    private async Task UseExcelSelectionAsync()
    {
        if (_main.WorkbookPath is not { } path) return;
        string listSheet = _workbook?.ListSheet ?? "";
        ExcelLive.ExcelSelection? selection;
        try
        {
            selection = await ExcelLive.RunSta(() =>
            {
                object? wb = ExcelLive.FindOpenWorkbook(path);
                if (wb is null) return null;
                try { return ExcelLive.Selection(wb); }
                finally { Marshal.ReleaseComObject(wb); }
            });
        }
        catch (Exception ex)
        {
            Message = "Could not read the selection in Excel: " + ex.Message;
            return;
        }
        if (selection is null)
        {
            Message = $"Open {Path.GetFileName(path)} in Excel and select the drawings there (rows of '{listSheet}', or cells with drawing numbers), then try again.";
            return;
        }

        var picked = Rows.Where(r =>
                (r.C.List is { } item && string.Equals(selection.Worksheet, listSheet, StringComparison.OrdinalIgnoreCase) && selection.Rows.Contains(item.Row))
                || selection.Texts.Any(t => string.Equals(ExcelCompare.NormalizeKey(t), ExcelCompare.NormalizeKey(r.Number), StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();
        if (picked.Count == 0)
        {
            Message = $"Nothing selected in Excel matches a drawing here. Select rows on '{listSheet}' (or cells with drawing numbers), then try again.";
            return;
        }
        foreach (var r in Rows) r.Push = picked.Contains(r);
        Filter = NeedsPush;
        int pushable = picked.Count(r => r.CanPush);
        Message = $"{picked.Count} drawing(s) selected in Excel; {pushable} of them have something to push. Only those are ticked now.";
    }

    /// <summary>Pushes the ticked values (and new sheets) from the list into the models, after a review.</summary>
    private async Task PushAsync()
    {
        var pushes = Rows.Where(r => r.C.List is not null && r.Push)
            .Select(r => new DeliverablePush(r.C.List!, r.IsNew, r.Fields.Where(f => f.Push).Select(f => f.ColumnId).ToList()))
            .Where(p => p.Create || p.ColumnIds.Count > 0)
            .ToList();
        if (pushes.Count == 0) return;
        IsBusy = true;
        try
        {
            string result = await _main.PushFromListAsync(pushes, Target);
            await RefreshAsync();
            Message = result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One drawing to push from the list: a new sheet, or these columns' list values into the model.</summary>
public sealed record DeliverablePush(Deliverable Item, bool Create, IReadOnlyList<string> ColumnIds);
