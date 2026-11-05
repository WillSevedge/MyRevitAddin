using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Excel;

namespace Nexus.Hub.ViewModels;

/// <summary>
/// The linked workbook (e.g. the Budget Tool) is the source of truth: Nexus compares its deliverables list with the
/// model (differences are fixed in the model), and the only thing it writes is the Model Export table.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _excelGate = new(1, 1);
    private FileSystemWatcher? _excelWatcher;
    private CancellationTokenSource? _excelSaveDelay;
    /// <summary>Saves Nexus makes itself are not reported as changes (UTC ticks).</summary>
    private long _ignoreExcelSavesUntil;
    private string _excelState = "";
    private DeliverablesViewModel? _deliverables;

    public string? WorkbookPath => _settings.ExcelWorkbook is { Length: > 0 } p ? p : null;
    public bool HasExcelLink => WorkbookPath is not null;
    public string ExcelLinkTitle => WorkbookPath is { } p ? Path.GetFileName(p) : "No workbook chosen";

    /// <summary>The loaded table, for the deliverables comparison.</summary>
    internal ResultTable? Table => _table;

    public RelayCommand UpdateExcelCommand => _updateExcel ??= new RelayCommand(() => ExportModelAsync(), () => _table is not null && HasExcelLink);
    private RelayCommand? _updateExcel;

    /// <summary>"Exported 10:42 · watching for saves" for the Excel card.</summary>
    public string ExcelState
    {
        get => _excelState;
        private set => Set(ref _excelState, value);
    }

    // ------------------------------------------------------------------ workbook

    /// <summary>Choose (or change) the workbook. Returns false when cancelled.</summary>
    internal bool ChooseWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the workbook with the deliverables list",
            Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            FileName = WorkbookPath ?? "",
        };
        if (dialog.ShowDialog() != true) return false;
        SetWorkbook(dialog.FileName);
        return true;
    }

    private void SetWorkbook(string? path)
    {
        _settings.ExcelWorkbook = path;
        _settings.Save();
        ExcelState = "";
        Raise(nameof(WorkbookPath));
        Raise(nameof(HasExcelLink));
        Raise(nameof(ExcelLinkTitle));
        WatchExcel();
    }

    /// <summary>Forgets the workbook (the file itself is not touched): no comparison, no export, no watching.</summary>
    public RelayCommand ClearWorkbookCommand => _clearWorkbook ??= new RelayCommand(() =>
    {
        string name = ExcelLinkTitle;
        _deliverables?.Window?.Close();
        SetWorkbook(null);
        Status = $"{name} is no longer used. Choose… picks a workbook again.";
        return Task.CompletedTask;
    }, () => HasExcelLink);
    private RelayCommand? _clearWorkbook;

    private Task LinkExcelAsync()
    {
        if (ChooseWorkbook()) Status = $"Using {ExcelLinkTitle}. Compare shows it next to the model.";
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ compare

    private Task CompareExcelAsync()
    {
        if (WorkbookPath is null && !ChooseWorkbook()) return Task.CompletedTask;
        if (_deliverables is not null)
        {
            _deliverables.Window?.Activate();
            return _deliverables.RefreshAsync();
        }
        var vm = new DeliverablesViewModel(this);
        var window = new DeliverablesWindow(vm) { Owner = Application.Current.MainWindow };
        vm.Window = window;
        _deliverables = vm;
        window.Closed += (_, _) => _deliverables = null;
        window.Show();
        return vm.RefreshAsync();
    }

    // ------------------------------------------------------------------ match the model to the list

    /// <summary>Open files new sheets can go in (programs that can create sheets: Revit).</summary>
    internal List<DocumentNode> SheetTargets() =>
        Agents.SelectMany(a => a.Documents).Where(d => d.Agent.Connection.CanCreateSheets).ToList();

    /// <summary>
    /// Pushes the deliverables list into the models (the workbook is never written): drawings not in the models
    /// become new sheets in <paramref name="target"/> (number, name, status), and the ticked list values of the others
    /// are staged as edits and written after the usual review (Review &amp; apply opens straight away).
    /// Returns what happened, for the deliverables window.
    /// </summary>
    internal async Task<string> PushFromListAsync(IReadOnlyList<DeliverablePush> pushes, DocumentNode? target)
    {
        if (_table is null || !IsSheetsView) return "Open Sheets & data › Sheets in the main window first.";
        var parts = new List<string>();
        var create = pushes.Where(p => p.Create).Select(p => p.Item).ToList();
        var values = pushes.Where(p => !p.Create && p.ColumnIds.Count > 0).ToList();
        // Values are staged on the rows as they are after any reload (new sheets): found again by number.
        if (create.Count > 0 && !ConfirmDiscardEdits()) return "Nothing changed.";

        if (create.Count > 0)
        {
            if (target is null)
                parts.Add($"{create.Count} new sheet(s) skipped: no open Revit model can take new sheets (open it in Revit with Nexus)");
            else if (!Dialogs.Confirm($"Create {create.Count} sheet(s) in {target.Title}?",
                         string.Join(", ", create.Take(12).Select(c => c.Number)) + (create.Count > 12 ? $" and {create.Count - 12} more" : "") +
                         ".\n\nEach gets the number, name and status from the list, and the title block the model's sheets use most. " +
                         "One Undo in Revit removes them all.", "Create sheets", kind: NoticeKind.Info, owner: _deliverables?.Window))
                parts.Add("new sheets skipped");
            else
                parts.Add(await CreateSheetsAsync(create, target));
        }

        int staged = 0;
        var refused = new List<string>();
        var table = _table;
        if (values.Count > 0 && table is not null)
        {
            var byNumber = DeliverablesCompare.Compare(values.Select(v => v.Item), table)
                .Where(c => c.Model is not null && c.List is not null)
                .ToDictionary(c => ExcelCompare.NormalizeKey(c.Number), StringComparer.OrdinalIgnoreCase);
            foreach (var push in values)
            {
                if (!byNumber.TryGetValue(ExcelCompare.NormalizeKey(push.Item.Number), out var c))
                {
                    refused.Add($"{push.Item.Number}: no longer in the loaded sheets");
                    continue;
                }
                foreach (var columnId in push.ColumnIds)
                {
                    string value = columnId == DeliverablesCompare.TitleId ? c.List!.Title
                        : columnId == DeliverablesCompare.RevId ? c.List!.Rev
                        : columnId == DeliverablesCompare.DateId ? c.List!.Date
                        : c.List!.Status;
                    if (value.Trim().Length == 0) continue; // an empty list cell does not clear the model
                    var reason = TrySetValue(c.Model!, columnId, value);
                    if (reason is null) staged++;
                    else refused.Add($"{c.Number}: {reason}");
                }
            }
        }
        if (refused.Count > 0)
            parts.Add($"{refused.Count} value(s) not pushed: " + string.Join("; ", refused.Take(4)) + (refused.Count > 4 ? "…" : ""));

        if (staged > 0)
        {
            // Write them now, with the usual review (one row per change, untick to leave one out).
            int before = PendingEdits;
            await ReviewAndApplyAsync();
            int left = PendingEdits;
            parts.Insert(0, left == 0 ? $"{staged} value(s) from the list written to the models"
                : left < before ? $"{before - left} value(s) written; {left} left pending (Review & apply in the main window)"
                : $"{staged} value(s) staged, not written yet (Review & apply in the main window writes them)");
        }
        if (parts.Count == 0) parts.Add("Nothing to push: the models already match the list for the ticked drawings");
        string message = string.Join(". ", parts) + ".";
        Status = message;
        return message;
    }

    /// <summary>Creates sheets for list drawings in <paramref name="target"/>, then reads the sheets again.</summary>
    private async Task<string> CreateSheetsAsync(IReadOnlyList<Deliverable> items, DocumentNode target)
    {
        var statusNames = string.Join("|", _sheetFields.Fields.FirstOrDefault(f => f.Name == "Status")?.Rules
            .Where(r => r.Host == HostKinds.Revit).SelectMany(r => r.Names) ?? new[] { "Sheet Status" });
        var request = new CreateSheetsRequest
        {
            DocumentId = target.Info.Id,
            Sheets = items.Select(c => new NewSheet
            {
                Number = c.Number, Name = c.Title,
                Values = c.Status.Length > 0 ? new Dictionary<string, string> { [statusNames] = c.Status } : new(),
            }).ToList(),
        };
        CreateSheetsResult result;
        try
        {
            Status = $"Creating {items.Count} sheet(s) in {target.Title}…";
            result = await target.Agent.Connection.CreateSheetsAsync(request);
        }
        catch (AgentRequestException ex)
        {
            HubLog.Warn($"Creating sheets in {target.Title} failed: {ex.Error}");
            return "No sheets were created: " + ex.Error.Message;
        }
        int created = result.Sheets.Count(x => x.Created);
        HubLog.Info($"{result.DocumentTitle}: created {created} sheet(s) with title block {result.TitleBlock}");
        string message = created > 0
            ? $"{created} sheet(s) created in {result.DocumentTitle} (title block {result.TitleBlock}; Undo in Revit: \"{result.UndoName}\")"
            : "no sheets were created";
        var problems = result.Sheets.Where(x => x.Message is not null).Select(x => $"{x.Number}: {x.Message}").ToList();
        if (problems.Count > 0) message += ". " + string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? "…" : "");
        if (created > 0)
        {
            // Read again so the new sheets are in the table.
            if (!target.IsChecked) target.Restore(true);
            await LoadAsync(confirm: false);
        }
        return message;
    }

    // ------------------------------------------------------------------ export to Model Export

    private sealed record ExcelPush(ExcelFillPlan Plan, bool Live, bool Written, string Backup);

    /// <summary>
    /// Fills the workbook's Model Export table with the loaded sheets: one row per sheet, sorted by program then
    /// number. Nothing else in the workbook is written. Rows of files that are not open now are kept.
    /// </summary>
    public async Task<bool> ExportModelAsync()
    {
        if (WorkbookPath is null && !ChooseWorkbook()) return false;
        string path = WorkbookPath!;
        string file = Path.GetFileName(path);
        var table = _table;
        if (table is null || !IsSheetsView || !table.ColumnsById.ContainsKey(SheetFieldMap.NumberColumnId))
        {
            Notify(NoticeKind.Info, "Open the Sheets view to export", $"Model Export in {file} is filled from the sheets of the ticked files.");
            return false;
        }
        if (!File.Exists(path))
        {
            Notify(NoticeKind.Warning, $"{file} was not found", path, "Choose workbook…", LinkExcelAsync);
            return false;
        }

        await _excelGate.WaitAsync();
        try
        {
            ExcelState = $"Exporting to {file}…";
            var info = await Task.Run(() => Deliverables.Read(path));
            if (info.ModelExport is not { } me)
            {
                ExcelState = "";
                Notify(NoticeKind.Warning, $"{file} has no Model Export table",
                    "Nexus fills an Excel table named ModelExport (or the table on a 'Model Export' tab). Nothing was written.");
                return false;
            }
            var link = Deliverables.ModelExportLink(path, me, table, _sheetFields);
            var writable = link.Columns.Where(c => ExcelSources.WritesExcel(c)).Select(c => c.Header).ToList();
            HubLog.Info($"Export to {file} › {me.Worksheet}: columns written: {(writable.Count == 0 ? "none" : string.Join(", ", writable))}; " +
                        $"not matched: {string.Join(", ", link.Columns.Where(c => c.ColumnId.Length == 0 && !c.IsFormula).Select(c => c.Header))}");
            if (writable.Count == 0)
            {
                ExcelState = "";
                Notify(NoticeKind.Warning, $"No Model Export column matches the sheet data",
                    $"The headers on '{me.Worksheet}' are: {string.Join(", ", me.Headers.Where(h => h.Length > 0))}. Nexus fills columns named like " +
                    "Sheet Number, Sheet Name, Rev, Rev Date, Status, Source or File. Nothing was written.");
                return false;
            }
            var memory = ExcelFillMemory.Load(path);
            bool firstTime = memory.Files.Count == 0;
            var open = Runs.Where(r => !r.Failed).Select(r => r.DocumentTitle).ToList();
            var rows = ExcelFill.Ordered(memory.Merge(ExcelFill.Rows(table, link), open), link);

            var push = await PushAsync(link, rows, allowClear: !firstTime);
            if (!push.Written && !push.Plan.IsEmpty)
            {
                // First export: rows already in Model Export that Nexus did not put there.
                if (!Dialogs.Confirm($"Replace the rows in Model Export?",
                        $"Model Export has {push.Plan.ClearedRows} row(s) that do not come from the files open now. They are replaced by the " +
                        $"{push.Plan.Rows} sheet(s) from Nexus. Formula columns are not touched.", "Replace", kind: NoticeKind.Info))
                {
                    ExcelState = "";
                    return false;
                }
                push = await PushAsync(link, rows, allowClear: true);
            }

            memory.Save(path);
            var plan = push.Plan;
            HubLog.Info($"Export to {file}: {rows.Count} sheet(s), {(push.Live ? "into the open workbook in Excel" : "into the file")}: {plan.Summary}");
            if (!push.Live && push.Written && await Task.Run(() => MissingAfterWrite(link, rows)) is { } missing)
            {
                ExcelState = "Last export did not stick";
                Notify(NoticeKind.Warning, $"Model Export in {file} does not show the sheets after writing", missing,
                    "Try again", () => ExportModelAsync());
                return false;
            }
            string time = DateTime.Now.ToString("t");
            ExcelState = plan.IsEmpty ? $"Model Export up to date · {time}" : $"Exported {time}{(push.Live ? " (in Excel: save it there)" : "")}";
            Status = plan.IsEmpty
                ? "Model Export already matches the model."
                : push.Live
                    ? $"Model Export: {plan.Summary}. The values are in Excel now; save the workbook there to keep them."
                    : $"Model Export: {plan.Summary}.";
            if (!plan.IsEmpty)
                HubLog.Info($"Exported to {file} › Model Export: {plan.Summary}{(push.Live ? " (in Excel)" : $". Backup: {push.Backup}")}");
            return true;
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Exporting to {file} failed.", ex);
            ExcelState = "Last export failed";
            Notify(NoticeKind.Warning, $"Model Export in {file} was not updated", ex.Message, "Try again", () => ExportModelAsync());
            return false;
        }
        finally
        {
            _excelGate.Release();
        }
    }

    /// <summary>Reads the file back: null when every exported sheet number is in Model Export, else what is missing.</summary>
    private static string? MissingAfterWrite(ExcelLink link, List<IReadOnlyDictionary<string, string>> rows)
    {
        if (link.KeyHeader is not { } keyHeader) return null;
        var expected = rows.Select(r => r.TryGetValue(keyHeader, out var k) ? ExcelCompare.NormalizeKey(k) : "").Where(k => k.Length > 0).ToList();
        if (expected.Count == 0) return null;
        var found = ExcelWorkbook.Read(link).Rows.Select(r => ExcelCompare.NormalizeKey(r.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lost = expected.Where(k => !found.Contains(k)).ToList();
        return lost.Count == 0 ? null
            : $"{lost.Count} of {expected.Count} sheet number(s) are not in the file after writing (e.g. {string.Join(", ", lost.Take(5))}). " +
              "If the workbook is open in Excel or syncing (OneDrive/SharePoint), close it there and export again.";
    }

    /// <summary>
    /// On an Excel thread: reads Model Export (as Excel shows it now when the workbook is open), plans the fill and
    /// writes it into Excel or the file, unless rows would be cleared without leave.
    /// </summary>
    private Task<ExcelPush> PushAsync(ExcelLink link, List<IReadOnlyDictionary<string, string>> rows, bool allowClear) =>
        ExcelLive.RunSta(() =>
        {
            var data = ExcelWorkbook.Read(link);
            object? workbook = ExcelLive.FindOpenWorkbook(link.WorkbookPath);
            // Open in Excel but out of reach: writing the file would be undone by Excel's copy (AutoSave or its next save).
            if (workbook is null && ExcelLive.OpenElsewhereReason(link.WorkbookPath) is { } reason)
            {
                string name = Path.GetFileName(link.WorkbookPath);
                throw new InvalidOperationException(
                    $"{name} looks open in Excel ({reason}), but Nexus cannot reach that Excel window, so nothing was written " +
                    "(Excel would put its own copy back). Open the workbook from its synced folder in File Explorer (not from Teams, " +
                    "SharePoint or a browser) and export again, or close it in Excel and export again. If Excel is closed, delete the " +
                    $"'~${name}' file next to it.");
            }
            try
            {
                if (workbook is not null) ExcelLive.Refresh(workbook, link, data);
                var plan = ExcelWorkbook.PlanFill(link, data, rows);
                if (plan.IsEmpty || (plan.ClearedRows > 0 && !allowClear))
                    return new ExcelPush(plan, workbook is not null, false, "");
                if (workbook is not null)
                {
                    ExcelLive.Apply(workbook, plan);
                    return new ExcelPush(plan, true, true, "");
                }
                IgnoreExcelSaves(TimeSpan.FromMinutes(2));
                try
                {
                    return new ExcelPush(plan, false, true, ExcelWorkbook.WriteFill(link, plan));
                }
                finally
                {
                    IgnoreExcelSaves(TimeSpan.FromSeconds(4));
                }
            }
            finally
            {
                if (workbook is not null) Marshal.ReleaseComObject(workbook);
            }
        });

    // ------------------------------------------------------------------ watching the workbook

    private void IgnoreExcelSaves(TimeSpan span) =>
        Interlocked.Exchange(ref _ignoreExcelSavesUntil, DateTime.UtcNow.Add(span).Ticks);

    /// <summary>Watches the workbook's folder for saves (Excel saves to a temporary file and renames it).</summary>
    private void WatchExcel()
    {
        _excelWatcher?.Dispose();
        _excelWatcher = null;
        if (WorkbookPath is not { } workbook) return;
        try
        {
            string path = Path.GetFullPath(workbook);
            string? folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) return;
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void OnEvent(string changed)
            {
                if (string.Equals(changed, path, StringComparison.OrdinalIgnoreCase))
                    Application.Current?.Dispatcher.BeginInvoke(() => OnExcelSaved(workbook));
            }
            watcher.Changed += (_, e) => OnEvent(e.FullPath);
            watcher.Created += (_, e) => OnEvent(e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(e.FullPath);
            watcher.Error += (_, e) => HubLog.Warn("Watching the workbook stopped.", e.GetException());
            watcher.EnableRaisingEvents = true;
            _excelWatcher = watcher;
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not watch the workbook.", ex);
        }
    }

    /// <summary>A save: wait until Excel has finished writing, then refresh the comparison or say what differs.</summary>
    private async void OnExcelSaved(string workbook)
    {
        _excelSaveDelay?.Cancel();
        var delay = _excelSaveDelay = new CancellationTokenSource();
        try { await Task.Delay(1500, delay.Token); }
        catch (OperationCanceledException) { return; }
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _ignoreExcelSavesUntil) || workbook != WorkbookPath) return;

        if (_deliverables is not null)
        {
            await _deliverables.RefreshAsync();
            return;
        }
        var table = _table;
        if (table is null || !IsSheetsView) return;
        string file = Path.GetFileName(workbook);
        try
        {
            var differences = await Task.Run(() =>
                DeliverablesCompare.Compare(Deliverables.Read(workbook).Items, table).Count(c => c.Result != DeliverableResult.Match));
            if (differences > 0)
                Notify(NoticeKind.Info, $"{file} was saved: {differences} drawing(s) differ from the model",
                    "Compare shows the deliverables list next to the model.", "Compare…", CompareExcelAsync);
            else
                Status = $"{file} was saved; the deliverables list matches the model.";
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Reading {file} after a save failed.", ex);
        }
    }
}
