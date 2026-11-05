using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Core.History;

namespace Nexus.Hub.ViewModels;

/// <summary>A grid column: the DataTable column it shows and how to label it.</summary>
public sealed record GridColumnSpec(string Column, string Group, string Name, bool Editable, bool Frozen);

/// <summary>
/// The hub screen: connected programs and files (left), a dataset loaded into an editable
/// grid (center), the selected row's properties (right), pending changes (bottom).
/// </summary>
public sealed partial class MainViewModel : Observable
{
    public const string SheetsDatasetId = "sheets";
    private const string RowIndexColumn = "__row";
    private const string FileColumn = "c_file";
    private const string ItemColumn = "c_item";
    private const int DefaultVisibleLimit = 25;

    private readonly Dictionary<int, AgentNode> _agents = new();
    private readonly Dictionary<(string Host, string Reader), ReaderNode> _readers = new();
    private readonly UiSettings _settings = UiSettings.Load();
    private readonly SheetFieldMap _sheetFields = SheetFieldMap.LoadOrCreate();

    // The grid: the flattened results, its DataTable (all columns), and grid column ↔ Nexus column id.
    private ResultTable? _table;
    private DataTable? _grid;
    private readonly Dictionary<string, string> _gridColumnIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gridColumnByColumnId = new(StringComparer.Ordinal);
    private HashSet<string> _visible = new(StringComparer.Ordinal);
    private bool _syncing;

    private DatasetNode? _dataset;
    private DataRowView? _selectedRow;
    private string _status = "Starting…";
    private string _summary = "";
    private string _search = "";
    private int _pendingEdits;
    private bool _busy;
    private bool _refreshing;
    private int _loadGeneration;
    private CancellationTokenSource? _reloadDelay;

    public MainViewModel()
    {
        Health = new HealthViewModel(this);
        _showDetails = _settings.InspectorPinned;
        _workspace = _settings.Workspace is Workspaces.Home or Workspaces.Connections ? _settings.Workspace : Workspaces.Data;
        RefreshCommand = new RelayCommand(async () => { await RefreshAsync(); await LoadAsync(); });
        ApplyChangesCommand = new RelayCommand(ReviewAndApplyAsync, () => _pendingEdits > 0 && !_busy);
        DiscardChangesCommand = new RelayCommand(() => { DiscardEdits(); return Task.CompletedTask; }, () => _pendingEdits > 0 && !_busy);
        ShowInModelCommand = new RelayCommand(() => ShowInModelAsync(SelectedRowsProvider?.Invoke() ?? Array.Empty<DataRowView>()),
            () => _table is not null);
        LinkExcelCommand = new RelayCommand(LinkExcelAsync);
        CompareExcelCommand = new RelayCommand(CompareExcelAsync, () => _table is not null);
        ExportExcelCommand = new RelayCommand(ExportExcel, () => _grid is not null);
        ExportCsvCommand = new RelayCommand(() => ExportLegacy("wide"), () => _table is not null);
        ExportLongCsvCommand = new RelayCommand(() => ExportLegacy("long"), () => _table is not null);
        ExportJsonCommand = new RelayCommand(() => ExportLegacy("json"), () => _table is not null);
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(NexusPaths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NexusPaths.LogsDir}\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });
        OpenSheetFieldsCommand = new RelayCommand(() =>
        {
            Process.Start(new ProcessStartInfo(Path.Combine(NexusPaths.Root, "sheet-fields.json")) { UseShellExecute = true });
            return Task.CompletedTask;
        });

        WatchExcel();

        HubLog.Message += line => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add(line);
            if (Log.Count > 2000) Log.RemoveAt(0);
        });
    }

    // ------------------------------------------------------------------ bindable state

    public ObservableCollection<AgentNode> Agents { get; } = new();
    public ObservableCollection<DatasetNode> Datasets { get; } = new();
    public ObservableCollection<DetailGroup> DetailGroups { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public List<ResultRun> Runs { get; private set; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyChangesCommand { get; }
    public RelayCommand DiscardChangesCommand { get; }
    public RelayCommand ShowInModelCommand { get; }
    public RelayCommand LinkExcelCommand { get; }
    public RelayCommand CompareExcelCommand { get; }
    public RelayCommand ExportExcelCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportLongCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenSheetFieldsCommand { get; }

    /// <summary>The window supplies the rows that are selected in the grid.</summary>
    public Func<IReadOnlyList<DataRowView>>? SelectedRowsProvider { get; set; }

    /// <summary>The grid's data changed (new load): rebuild the columns.</summary>
    public event Action<DataTable, IReadOnlyList<GridColumnSpec>>? TableReady;
    /// <summary>Only the visible columns changed.</summary>
    public event Action<IReadOnlyList<GridColumnSpec>>? ColumnsChanged;
    /// <summary>Raised before edits are collected; the window commits any cell still being edited.</summary>
    public event Action? CommitGridEdits;
    /// <summary>One-line summary of connected programs (tray tooltip).</summary>
    public event Action<string>? HostsChanged;

    public DatasetNode? SelectedDataset
    {
        get => _dataset;
        set
        {
            // WPF clears the selection while the list is rebuilt; that is not the user choosing nothing.
            if (value is null || ReferenceEquals(value, _dataset)) return;
            if (!ConfirmDiscardEdits())
            {
                Raise(); // put the picker back
                return;
            }
            _dataset = value;
            Raise();
            Raise(nameof(WorkspaceTitle));
            Raise(nameof(DatasetOptions));
            Raise(nameof(HasOptions));
            if (value is not null)
            {
                _settings.LastDataset = value.Id;
                _settings.Save();
            }
            _ = LoadAsync(confirm: false);
        }
    }

    /// <summary>Options of the readers behind the selected dataset.</summary>
    public IEnumerable<ReaderNode> DatasetOptions => _dataset?.Readers.Values.Where(r => r.Options.Count > 0) ?? Enumerable.Empty<ReaderNode>();
    public bool HasOptions => DatasetOptions.Any();

    public DataRowView? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            BuildDetails();
            Raise(nameof(SelectedTitle));
            Raise(nameof(SelectedSubtitle));
        }
    }

    public string SelectedTitle => RowOf(_selectedRow) is { } r ? (r.Key.Length > 0 ? r.Key : r.Item) : "Nothing selected";
    public string SelectedSubtitle => RowOf(_selectedRow) is { } r ? $"{r.ItemType} · {r.Document} · {r.Host}" : "Select a row to see and edit all of its properties.";

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ApplySearch();
        }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    // ------------------------------------------------------------------ info bar

    private string _noticeTitle = "";
    private string _noticeText = "";
    private NoticeKind _noticeKind;

    public string NoticeTitle { get => _noticeTitle; private set => Set(ref _noticeTitle, value); }
    public string NoticeText { get => _noticeText; private set => Set(ref _noticeText, value); }
    public NoticeKind NoticeKind { get => _noticeKind; private set => Set(ref _noticeKind, value); }
    public bool HasNotice => _noticeTitle.Length > 0;

    /// <summary>
    /// Shows a message in the info bar above the table (instead of a pop-up), optionally with a button
    /// (<paramref name="actionText"/>) that closes the message and runs <paramref name="action"/>.
    /// </summary>
    public void Notify(NoticeKind kind, string title, string text = "", string? actionText = null, Func<Task>? action = null)
    {
        NoticeKind = kind;
        NoticeText = text;
        NoticeTitle = title;
        _noticeAction = action;
        NoticeActionText = action is null ? "" : actionText ?? "OK";
        Raise(nameof(HasNotice));
        Raise(nameof(HasNoticeAction));
    }

    private Func<Task>? _noticeAction;
    private string _noticeActionText = "";
    public string NoticeActionText { get => _noticeActionText; private set => Set(ref _noticeActionText, value); }
    public bool HasNoticeAction => _noticeAction is not null;

    public RelayCommand NoticeActionCommand => _noticeActionCommand ??= new RelayCommand(async () =>
    {
        var action = _noticeAction;
        CloseNotice();
        if (action is not null) await action();
    });
    private RelayCommand? _noticeActionCommand;

    public RelayCommand DismissNoticeCommand => _dismissNotice ??= new RelayCommand(() =>
    {
        CloseNotice();
        return Task.CompletedTask;
    });
    private RelayCommand? _dismissNotice;

    private void CloseNotice()
    {
        NoticeTitle = "";
        _noticeAction = null;
        Raise(nameof(HasNotice));
        Raise(nameof(HasNoticeAction));
    }

    /// <summary>"3 files · 42 rows · 2 warnings".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public int PendingEdits
    {
        get => _pendingEdits;
        private set
        {
            if (!Set(ref _pendingEdits, value)) return;
            Raise(nameof(HasPendingEdits));
            Raise(nameof(PendingText));
        }
    }

    public bool HasPendingEdits => _pendingEdits > 0;

    private bool _showDetails = true;

    /// <summary>Details pane on the right (toggled from the command bar).</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (Set(ref _showDetails, value) && value) Health.IsOpen = false;
        }
    }

    /// <summary>At least one program is connected (the Data section of the sidebar shows).</summary>
    public bool HasPrograms => Agents.Count > 0;
    public bool NoPrograms => Agents.Count == 0;

    /// <summary>Nothing to show yet: the grid is replaced by a short explanation.</summary>
    public bool IsEmpty => !_busy && (_table is null || _table.Rows.Count == 0);

    public string EmptyTitle => Agents.Count == 0 ? "Waiting for your programs"
        : Runs.Count == 0 ? "No files selected"
        : Runs.All(r => r.Failed) ? "Could not read the files"
        : "Nothing found";

    public string EmptyText => Agents.Count == 0
        ? "Open Revit, AutoCAD, Civil 3D or Plant 3D with the Nexus add-in. They appear on the left within a few seconds."
        : Runs.Count == 0
            ? $"Tick one or more files on the left to see their {(_dataset?.Title ?? "data").ToLowerInvariant()}."
            : Runs.All(r => r.Failed)
                ? string.Join(Environment.NewLine, Runs.Select(r => $"{r.DocumentTitle}: {r.Error!.Message}"))
                : $"The selected files have no {(_dataset?.Title ?? "data").ToLowerInvariant()}.";
    public string PendingText => _pendingEdits == 1 ? "1 change not yet applied" : $"{_pendingEdits} changes not yet applied";

    public bool HasIssues => Runs.Any(r => r.Failed || r.Result?.Warnings.Count > 0);

    public string IssuesText
    {
        get
        {
            int failed = Runs.Count(r => r.Failed), warnings = Runs.Sum(r => r.Result?.Warnings.Count ?? 0);
            var parts = new List<string>();
            if (failed > 0) parts.Add($"{failed} failed");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            return string.Join(", ", parts);
        }
    }

    // ------------------------------------------------------------------ programs and files

    /// <summary>First start: find the programs, then load the last dataset (Sheets by default).</summary>
    public async Task StartAsync()
    {
        Nexus.Hub.Core.Files.WorkCopy.CleanUp();
        await RefreshAsync();
        await LoadAsync(confirm: false);
    }

    /// <summary>
    /// Opens the view backed by a reader (asked for from a program's ribbon, e.g. Fabrication parts),
    /// waiting briefly for the program to be found when the hub has just started.
    /// </summary>
    public async Task ShowReaderAsync(string readerId)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var dataset = Datasets.FirstOrDefault(d => d.Readers.Values.Any(r => r.Descriptor.Id.Equals(readerId, StringComparison.OrdinalIgnoreCase)));
            if (dataset is not null)
            {
                SelectedDataset = dataset;
                return;
            }
            await RefreshAsync();
            if (Datasets.Any(d => d.Readers.Values.Any(r => r.Descriptor.Id.Equals(readerId, StringComparison.OrdinalIgnoreCase)))) continue;
            await Task.Delay(500);
        }
        Status = $"No connected program offers '{readerId}'. Is its Nexus add-in up to date?";
    }

    public async Task RefreshAsync()
    {
        var found = AgentDiscovery.Discover();

        foreach (var pid in _agents.Keys.Where(pid => found.All(f => f.Host.ProcessId != pid)).ToList())
        {
            var gone = _agents[pid];
            _agents.Remove(pid);
            Agents.Remove(gone);
            await gone.Connection.DisposeAsync();
        }

        await Task.WhenAll(found.Select(reg =>
        {
            if (!_agents.TryGetValue(reg.Host.ProcessId, out var node))
            {
                node = new AgentNode(new AgentConnection(reg));
                _agents[reg.Host.ProcessId] = node;
                Agents.Add(node);
            }
            return RefreshAgentAsync(node, reg);
        }));

        // Number sessions of the same program ("Revit 2026 #2") instead of showing process ids.
        foreach (var same in Agents.GroupBy(a => a.Connection.Host.Name))
        {
            int n = 0;
            foreach (var a in same.OrderBy(a => a.Connection.Host.ProcessStartUtc))
            {
                a.Instance = ++n;
                a.HasSiblings = same.Count() > 1;
                a.Refreshed();
            }
        }

        RebuildDatasets();
        if (Agents.Any(a => a.Connection.IsBusy)) RetryBusySoon();
        Raise(nameof(EmptyTitle));
        Raise(nameof(HasPrograms));
        Raise(nameof(NoPrograms));
        Raise(nameof(EmptyText));
        RaiseConnections();
        int docs = Agents.Sum(a => a.Documents.Count);
        if (!_busy)
            Status = found.Count == 0
                ? "Waiting for Revit, AutoCAD, Civil 3D or Plant 3D (with the Nexus add-in). They appear here automatically."
                : $"Connected to {found.Count} program(s) with {docs} open file(s).";
        HostsChanged?.Invoke(found.Count == 0
            ? "waiting for Revit, AutoCAD, Civil 3D or Plant 3D"
            : string.Join(", ", Agents.Select(a => a.Title)));
    }

    private async Task RefreshAgentAsync(AgentNode node, AgentRegistration reg)
    {
        await node.Connection.RefreshAsync(reg);
        var checkedIds = node.Documents.Where(d => d.IsChecked).Select(d => d.Info.Id).ToHashSet();
        bool first = node.Documents.Count == 0;
        foreach (var d in node.Documents) d.CheckedChanged -= OnDocumentChecked;
        node.Documents.Clear();
        foreach (var info in node.Connection.Documents)
        {
            var doc = new DocumentNode(node, info);
            // First time: tick the active file of each program; afterwards keep the user's ticks.
            doc.Restore(first ? info.IsActive : checkedIds.Contains(info.Id));
            doc.CheckedChanged += OnDocumentChecked;
            node.Documents.Add(doc);
        }
        node.Refreshed();
    }

    /// <summary>
    /// Cheap poll (registration files): refreshes only when a program started or stopped, or always
    /// with <paramref name="force"/> (re-lists open files). Reloads when the ticked files changed.
    /// </summary>
    public async Task RefreshIfHostsChangedAsync(bool force = false)
    {
        if (_busy || _refreshing) return;
        if (!force)
        {
            var pids = AgentDiscovery.Discover().Select(r => r.Host.ProcessId).ToHashSet();
            if (pids.SetEquals(_agents.Keys)) return;
        }
        _refreshing = true;
        try
        {
            var before = CheckedDocumentKeys();
            await RefreshAsync();
            // New program with its active file ticked, or a program closed: reload (unless edits are pending).
            if (_pendingEdits == 0 && !before.SetEquals(CheckedDocumentKeys())) await LoadAsync(confirm: false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool _busyRetryPending;

    /// <summary>A program was busy (e.g. opening a model): ask it again shortly, until it answers.</summary>
    private void RetryBusySoon()
    {
        if (_busyRetryPending) return;
        _busyRetryPending = true;
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            _busyRetryPending = false;
            if (_busy || _refreshing)
            {
                RetryBusySoon();
                return;
            }
            await RefreshIfHostsChangedAsync(force: true);
        }), TaskScheduler.Default);
    }

    private HashSet<string> CheckedDocumentKeys() =>
        Agents.SelectMany(a => a.Documents).Where(d => d.IsChecked).Select(d => $"{d.Agent.Connection.Host.ProcessId}/{d.Info.Id}").ToHashSet();

    private void OnDocumentChecked(DocumentNode doc)
    {
        Raise(nameof(TickedFileCount));
        // Debounce: ticking several files reloads once.
        _reloadDelay?.Cancel();
        var cts = _reloadDelay = new CancellationTokenSource();
        _ = Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Application.Current.Dispatcher.BeginInvoke(() => _ = LoadAsync());
        }, TaskScheduler.Default);
    }

    private void RebuildDatasets()
    {
        string? selectedId = _dataset?.Id ?? _settings.LastDataset ?? SheetsDatasetId;
        var datasets = new List<DatasetNode>();

        var sheets = new DatasetNode
        {
            Id = SheetsDatasetId,
            Title = "Sheets",
            Category = "Shared",
            IsSheetIndex = true,
            Description = "Every sheet: Revit sheets and AutoCAD/Civil 3D/Plant 3D layouts with their title block. " +
                          "Number, Title, Revision, Drawn By... are matched in each program (edit the matching in sheet-fields.json).",
        };
        datasets.Add(sheets);

        foreach (var agent in Agents)
        {
            var host = agent.Connection.Host;
            foreach (var d in agent.Connection.Readers.Where(r => r.IsImplemented))
            {
                var reader = ReaderFor(host, d);
                if (d.Id is "revit.sheets" or "acad.sheets")
                {
                    sheets.Readers.TryAdd(host.HostKind, reader);
                    continue;
                }
                string id = $"{host.HostKind}:{d.Id}";
                if (datasets.Any(x => x.Id == id)) continue;
                var ds = new DatasetNode
                {
                    Id = id,
                    Title = Products.TitleCase(d.DisplayName),
                    Category = reader.HostLabel,
                    Description = d.Description,
                };
                ds.Readers[host.HostKind] = reader;
                datasets.Add(ds);
            }
        }

        // Keep the same objects when nothing changed, so the picker does not flicker.
        bool same = datasets.Count == Datasets.Count && datasets.Zip(Datasets).All(p => p.First.Id == p.Second.Id
            && p.First.Readers.Keys.OrderBy(k => k).SequenceEqual(p.Second.Readers.Keys.OrderBy(k => k)));
        if (same) return;

        Datasets.Clear();
        foreach (var d in datasets) Datasets.Add(d);
        var keep = Datasets.FirstOrDefault(d => d.Id == selectedId) ?? Datasets.First();
        _dataset = keep;
        Raise(nameof(SelectedDataset));
        Raise(nameof(DatasetOptions));
        Raise(nameof(HasOptions));
    }

    private ReaderNode ReaderFor(HostInfo host, ReaderDescriptor d)
    {
        if (!_readers.TryGetValue((host.HostKind, d.Id), out var node))
        {
            node = new ReaderNode(d, host.HostKind, Products.ReaderLabel(host, d.Id));
            _readers[(host.HostKind, d.Id)] = node;
        }
        return node;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Reads the selected dataset from every ticked file and shows it.</summary>
    public async Task LoadAsync(bool confirm = true)
    {
        var dataset = _dataset;
        if (dataset is null) return;
        if (confirm && !ConfirmDiscardEdits()) return;
        int generation = ++_loadGeneration;

        var docs = Agents.SelectMany(a => a.Documents)
            .Where(d => d.IsChecked && dataset.Readers.ContainsKey(d.Agent.Connection.Host.HostKind)
                        && d.Agent.Connection.Readers.Any(r => r.Id == dataset.Readers[d.Agent.Connection.Host.HostKind].Descriptor.Id))
            .ToList();

        if (docs.Count == 0)
        {
            Runs = new List<ResultRun>();
            ShowResults(dataset);
            Status = Agents.Count == 0
                ? "Waiting for Revit, AutoCAD, Civil 3D or Plant 3D. They appear on the left automatically."
                : $"Tick a file on the left to see its {dataset.Title.ToLowerInvariant()}.";
            return;
        }

        _busy = true;
        RaiseEmptyState();
        Status = $"Reading {dataset.Title.ToLowerInvariant()} from {docs.Count} file(s)…";
        var work = BeginWork($"Reading {dataset.Title.ToLowerInvariant()}", docs.Count);
        var readTimer = Stopwatch.StartNew();
        try
        {
            var perAgent = docs.GroupBy(d => d.Agent).Select(async group =>
            {
                var runs = new List<ResultRun>();
                var reader = dataset.Readers[group.Key.Connection.Host.HostKind];
                foreach (var doc in group)
                {
                    runs.Add(await ReadOneAsync(group.Key, doc.Info.Title, new ReadRequest
                    {
                        DocumentId = doc.Info.Id,
                        ReaderId = reader.Descriptor.Id,
                        Options = reader.OptionValues(),
                    }, work.Token));
                    StepWork(work);
                }
                return runs;
            });
            var all = (await Task.WhenAll(perAgent)).SelectMany(r => r).ToList();
            if (generation != _loadGeneration) return; // a newer load started meanwhile
            Runs = all;
            HubLog.Info($"Read {dataset.Title} from {docs.Count} file(s) in {readTimer.ElapsedMilliseconds} ms " +
                        $"({all.Sum(r => r.Result?.Items.Count ?? 0)} items, {all.Sum(r => r.Result?.Items.Sum(i => i.Groups.Sum(g => g.Properties.Count)) ?? 0)} values)");
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            if (generation == _loadGeneration)
                Status = "Reading cancelled. The table shows what was loaded before. (The program may finish its read in the background.)";
            return;
        }
        finally
        {
            if (generation == _loadGeneration) _busy = false;
            EndWork(work);
            RaiseEmptyState();
        }
        ShowResults(dataset);
    }

    // ------------------------------------------------------------------ progress and cancel

    private CancellationTokenSource? _work;
    private int _workDone, _workTotal;
    private string _workText = "";

    /// <summary>A read or an apply is running (progress bar and Cancel are shown).</summary>
    public bool IsWorking => _work is not null;
    public double WorkProgress => _workTotal == 0 ? 0 : 100.0 * _workDone / _workTotal;
    /// <summary>One file: no meaningful percentage, show an animated bar.</summary>
    public bool WorkIndeterminate => _workTotal <= 1;
    public string WorkText => _workTotal > 1 ? $"{_workText}  ({_workDone} of {_workTotal} files)" : _workText + "…";

    public RelayCommand CancelWorkCommand => _cancelWork ??= new RelayCommand(() =>
    {
        _work?.Cancel();
        Status = "Cancelling…";
        return Task.CompletedTask;
    }, () => _work is { IsCancellationRequested: false });
    private RelayCommand? _cancelWork;

    /// <summary>Starts a cancellable operation over <paramref name="total"/> files (cancels one still running).</summary>
    private CancellationTokenSource BeginWork(string text, int total)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        _workText = text;
        _workTotal = total;
        _workDone = 0;
        RaiseWork();
        return _work;
    }

    private void StepWork(CancellationTokenSource work)
    {
        if (!ReferenceEquals(work, _work)) return;
        _workDone++;
        RaiseWork();
    }

    private void EndWork(CancellationTokenSource work)
    {
        if (ReferenceEquals(work, _work)) _work = null;
        work.Dispose();
        RaiseWork();
    }

    private void RaiseWork()
    {
        Raise(nameof(IsWorking));
        Raise(nameof(WorkProgress));
        Raise(nameof(WorkIndeterminate));
        Raise(nameof(WorkText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private static async Task<ResultRun> ReadOneAsync(AgentNode agent, string documentTitle, ReadRequest request, CancellationToken ct = default)
    {
        var host = agent.Connection.Host;
        try
        {
            var result = await agent.Connection.ReadAsync(request, ct);
            HubLog.Info($"{host.DisplayName}: {request.ReaderId} on {documentTitle}: {result.Items.Count} items in {result.ElapsedMs} ms");
            return new ResultRun { Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId, Agent = agent, Request = request, Result = result };
        }
        catch (AgentRequestException ex)
        {
            HubLog.Warn($"{host.DisplayName}: {request.ReaderId} on {documentTitle}: {ex.Error}");
            return new ResultRun { Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId, Agent = agent, Request = request, Error = ex.Error };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            HubLog.Error($"{host.DisplayName}: {request.ReaderId} on {documentTitle} failed", ex);
            return new ResultRun
            {
                Host = host, DocumentTitle = documentTitle, ReaderId = request.ReaderId, Agent = agent, Request = request,
                Error = new ErrorInfo(ErrorCodes.Disconnected, ex.Message),
            };
        }
    }

    private void ShowResults(DatasetNode dataset)
    {
        BuildTable(dataset);
        RaiseEmptyState();
        Raise(nameof(EmptyTitle));
        Raise(nameof(HasPrograms));
        Raise(nameof(NoPrograms));
        Raise(nameof(EmptyText));
        Raise(nameof(HasRevisionColumns));
        int files = Runs.Count(r => !r.Failed);
        Summary = _table is null ? "" : $"{files} file{(files == 1 ? "" : "s")} · {_table.Rows.Count} row{(_table.Rows.Count == 1 ? "" : "s")}";
        Raise(nameof(HasIssues));
        Raise(nameof(IssuesText));
        if (Runs.Count > 0)
            Status = Runs.Any(r => r.Failed)
                ? $"Some files could not be read: {string.Join("; ", Runs.Where(r => r.Failed).Select(r => $"{r.DocumentTitle}: {r.Error!.Message}"))}"
                : "Double-click a value to edit it. Edited values turn yellow until you apply them.";
    }

    // ------------------------------------------------------------------ grid

    private void BuildTable(DatasetNode dataset)
    {
        var timer = Stopwatch.StartNew();
        if (_grid is not null)
        {
            _grid.ColumnChanged -= OnGridValueChanged;
            _grid.RowChanged -= OnGridRowChanged;
        }
        _gridColumnIds.Clear();
        _gridColumnByColumnId.Clear();
        SelectedRow = null;

        var table = ResultTable.Build(Runs.Select(r => r.ToSource()).OfType<ResultSource>(),
            includeChildren: true, sheetFields: dataset.IsSheetIndex ? _sheetFields : null);

        var dt = new DataTable();
        dt.Columns.Add(RowIndexColumn, typeof(int));
        dt.Columns.Add(FileColumn, typeof(string));
        dt.Columns.Add(ItemColumn, typeof(string));
        for (int i = 0; i < table.Columns.Count; i++)
        {
            string name = "c" + i;
            dt.Columns.Add(name, typeof(string));
            _gridColumnIds[name] = table.Columns[i].Id;
            _gridColumnByColumnId[table.Columns[i].Id] = name;
        }

        dt.BeginLoadData();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var values = new object?[3 + table.Columns.Count];
            values[0] = r;
            values[1] = row.Document;
            values[2] = new string(' ', row.Depth * 3) + row.Item;
            for (int c = 0; c < table.Columns.Count; c++)
                values[3 + c] = row.Values.TryGetValue(table.Columns[c].Id, out var v) ? v.Value : null;
            dt.Rows.Add(values);
        }
        dt.EndLoadData();
        dt.AcceptChanges();
        dt.ColumnChanged += OnGridValueChanged;
        dt.RowChanged += OnGridRowChanged;

        _table = table;
        _grid = dt;
        _rowIndex = new Dictionary<TableRow, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < table.Rows.Count; i++) _rowIndex[table.Rows[i]] = i;
        PendingEdits = 0;
        _visible = InitialVisibleColumns(dataset, table);
        Health.Reset(dataset.IsSheetIndex);
        long built = timer.ElapsedMilliseconds;
        TableReady?.Invoke(dt, ColumnSpecs(dataset));
        long shown = timer.ElapsedMilliseconds - built;
        ApplySearch();
        DataChanged?.Invoke();
        // Where the time goes after the programs answer (the read itself is logged per file).
        HubLog.Info($"Table {dataset.Title}: {table.Rows.Count} rows × {table.Columns.Count} columns; built in {built} ms, grid {shown} ms, " +
                    $"checks {timer.ElapsedMilliseconds - built - shown} ms");
    }

    // ------------------------------------------------------------------ for the health checks

    private Dictionary<TableRow, int> _rowIndex = new(ReferenceEqualityComparer.Instance);

    /// <summary>Sheet health checks over the loaded sheets.</summary>
    public HealthViewModel Health { get; }

    /// <summary>The table was loaded, or a value in it changed (edits count before they are applied).</summary>
    public event Action? DataChanged;

    /// <summary>Asks the window to select a cell (row index in the table, grid column) and scroll to it.</summary>
    public event Action<int, string>? FocusCellRequested;

    public IReadOnlyList<TableRow> LoadedRows => _table?.Rows ?? (IReadOnlyList<TableRow>)Array.Empty<TableRow>();

    public int RowIndexOf(TableRow row) => _rowIndex.TryGetValue(row, out int i) ? i : -1;

    public static int RowIndex(DataRowView view) => view.Row[RowIndexColumn] is int i ? i : -1;

    public TableRow? TableRowOf(DataRowView view) => RowOf(view);

    /// <summary>
    /// What Rename &amp; Renumber can change: Sheet Number and Sheet Name only (each under its one name in every
    /// program: the Revit parameter or the AutoCAD title block attribute). Sheet numbers are unique.
    /// </summary>
    public List<RenameField> RenameFields(IReadOnlyList<TableRow> rows)
    {
        var fields = new List<RenameField>();
        if (_table is null) return fields;
        foreach (var field in new[] { SheetFieldMap.NumberField, "Title" })
        {
            string id = SheetFieldMap.ColumnId(field);
            bool editable = rows.Any(r => r.Values.TryGetValue(id, out var v) && v.Source != PropertySource.Derived && Editing.Blocker(r, id) is null);
            if (_table.ColumnsById.ContainsKey(id) && editable)
                fields.Add(new RenameField(id, SheetFieldMap.DisplayName(field), Unique: field == SheetFieldMap.NumberField));
        }
        return fields;
    }

    /// <summary>Every column of the table (id and the name users see), for {Column} values in Rename &amp; Renumber.</summary>
    public IReadOnlyList<(string Id, string Label)> TableColumns =>
        _table?.Columns.Select(c => (c.Id, c.Label)).ToList() ?? new List<(string, string)>();

    /// <summary>The value shown in the grid (including edits not applied yet).</summary>
    public string? CurrentValue(TableRow row, string columnId)
    {
        if (_grid is null || !_gridColumnByColumnId.TryGetValue(columnId, out var column)) return null;
        int i = RowIndexOf(row);
        if (i < 0 || i >= _grid.Rows.Count) return null;
        var dataRow = _grid.Rows[i];
        var version = dataRow.HasVersion(DataRowVersion.Proposed) ? DataRowVersion.Proposed : DataRowVersion.Current;
        return dataRow[column, version] as string;
    }

    /// <summary>Selects the cell for a property; if its column is hidden, the visible column showing the same property.</summary>
    public void FocusCell(TableRow row, string? columnId)
    {
        int i = RowIndexOf(row);
        if (i < 0 || _table is null) return;
        string? target = columnId is not null && _visible.Contains(columnId) ? columnId : null;
        if (target is null && columnId is not null && row.Values.TryGetValue(columnId, out var property))
            target = _table.Columns.Select(c => c.Id)
                .FirstOrDefault(id => _visible.Contains(id) && row.Values.TryGetValue(id, out var other) && ReferenceEquals(other, property));
        FocusCellRequested?.Invoke(i, target is not null && _gridColumnByColumnId.TryGetValue(target, out var g) ? g : ItemColumn);
    }

    private HashSet<string> InitialVisibleColumns(DatasetNode dataset, ResultTable table)
    {
        if (_settings.VisibleColumns.TryGetValue(ColumnsKey(dataset, table), out var saved))
        {
            var kept = saved.Where(table.ColumnsById.ContainsKey).ToHashSet(StringComparer.Ordinal);
            if (kept.Count > 0) return kept;
        }
        if (dataset.IsSheetIndex)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            bool revit = table.Rows.Any(r => r.Source.Host.HostKind == HostKinds.Revit);
            bool autocad = table.Rows.Any(r => r.Source.Host.HostKind != HostKinds.Revit);
            // Revit and AutoCAD together: only the standard columns (Sheet Number, Sheet Name, Revision...),
            // which every program fills. Each program's own parameters would be blank on the others' rows.
            if (revit && autocad)
            {
                fields.UnionWith(table.Columns.Where(c => c.Group == SheetFieldMap.Group).Select(c => c.Id));
                if (fields.Count > 0) return fields;
            }
            // With AutoCAD layouts in the view, the standard columns (Number, Title, Revision...) line the programs up.
            if (autocad)
                fields.UnionWith(table.Columns.Where(c => c.Group == SheetFieldMap.Group).Select(c => c.Id));
            // Revit: the sheet's parameters as the Properties palette shows them (Graphics, Text, Identity Data,
            // Other..., project and shared parameters included). Revisions on each sheet stay a right-click.
            if (revit)
                fields.UnionWith(table.Columns.Where(c => c.Group.StartsWith(RevitSheetGroupPrefix, StringComparison.Ordinal)
                                                          && c.Group != RevitSheetGroupPrefix + RevitHiddenGroup).Select(c => c.Id));
            // AutoCAD: the title block attributes.
            if (autocad)
                fields.UnionWith(table.Columns.Where(c => c.Group == "Title Block" && !IsComputedOnly(table, c.Id)).Select(c => c.Id));
            if (fields.Count > 0) return fields;
        }
        var props = table.Columns.Where(c => !IsComputedOnly(table, c.Id)).Select(c => c.Id).ToList();
        return (props.Count <= 40 ? props : props.Take(DefaultVisibleLimit)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Where the column choice is saved: per view, and per mix of programs when the view has more than one
    /// (Revit alone, AutoCAD alone and both together each keep their own columns).
    /// </summary>
    private static string ColumnsKey(DatasetNode dataset, ResultTable? table)
    {
        var kinds = table?.Rows.Select(r => r.Source.Host.HostKind).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        return kinds.Count > 1 ? dataset.Id + "|" + string.Join("+", kinds) : dataset.Id;
    }

    /// <summary>Group prefix of a Revit sheet's own parameters, and the group of those the palette does not show.</summary>
    private const string RevitSheetGroupPrefix = "Sheet · ";
    private const string RevitHiddenGroup = "Not in Properties";

    private static bool IsComputedOnly(ResultTable table, string columnId) =>
        table.Rows.All(r => !r.Values.TryGetValue(columnId, out var v) || v.Source == PropertySource.Derived);

    /// <summary>The grid columns as they are now (for rebuilding the grid's columns).</summary>
    public IReadOnlyList<GridColumnSpec> CurrentColumnSpecs() => ColumnSpecs(_dataset);

    private List<GridColumnSpec> ColumnSpecs(DatasetNode? dataset)
    {
        var specs = new List<GridColumnSpec> { new(FileColumn, "", "File", false, true) };
        if (dataset is not { IsSheetIndex: true }) specs.Add(new GridColumnSpec(ItemColumn, "", "Item", false, true));
        if (_table is null) return specs;
        foreach (var c in _table.Columns.Where(c => _visible.Contains(c.Id)))
            specs.Add(new GridColumnSpec(_gridColumnByColumnId[c.Id], c.Group, c.Label, true, false));
        return specs;
    }

    /// <summary>Column chooser data for the current table.</summary>
    public List<ColumnGroupNode> BuildColumnGroups()
    {
        var groups = new List<ColumnGroupNode>();
        if (_table is null) return groups;
        foreach (var g in _table.Columns.GroupBy(c => c.Group))
        {
            var node = new ColumnGroupNode(g.Key);
            foreach (var key in g) node.Columns.Add(new ColumnNode(node, key) { IsChecked = _visible.Contains(key.Id) });
            node.ChildChanged();
            groups.Add(node);
        }
        return groups;
    }

    public void SetVisibleColumns(IEnumerable<string> columnIds)
    {
        _visible = columnIds.ToHashSet(StringComparer.Ordinal);
        if (_dataset is not null)
        {
            _settings.VisibleColumns[ColumnsKey(_dataset, _table)] = _visible.ToList();
            _settings.Save();
        }
        ColumnsChanged?.Invoke(ColumnSpecs(_dataset));
        ApplySearch();
    }

    /// <summary>Filters rows to those containing the search text in any visible column.</summary>
    private void ApplySearch()
    {
        if (_grid is null) return;
        string s = _search.Trim();
        if (s.Length == 0)
        {
            _grid.DefaultView.RowFilter = "";
            return;
        }
        string like = EscapeLike(s);
        var cols = new[] { FileColumn, ItemColumn }.Concat(_visible.Where(_gridColumnByColumnId.ContainsKey).Select(id => _gridColumnByColumnId[id]));
        try
        {
            _grid.DefaultView.RowFilter = string.Join(" OR ", cols.Select(c => $"[{c}] LIKE '%{like}%'"));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Search filter failed.", ex);
        }
    }

    private static string EscapeLike(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s)
        {
            sb.Append(ch switch
            {
                '\'' => "''",
                '[' => "[[]",
                ']' => "[]]",
                '*' => "[*]",
                '%' => "[%]",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    public bool IsPropertyColumn(string gridColumn) => _gridColumnIds.ContainsKey(gridColumn);

    private TableRow? RowOf(DataRowView? view) =>
        view is not null && _table is not null && view.Row.Table == _grid && view.Row[RowIndexColumn] is int i && i < _table.Rows.Count
            ? _table.Rows[i]
            : null;

    // ------------------------------------------------------------------ revisions on sheets

    /// <summary>Group of the per-revision Yes/No columns on Revit sheets.</summary>
    public const string RevisionsGroup = "Revisions on Sheet";

    public bool HasRevisionColumns => _table?.Columns.Any(c => c.Group == RevisionsGroup) == true;

    /// <summary>
    /// For the given sheet rows: each revision, and whether it is on all of them (true), none (false)
    /// or some (null). Rows without revision columns (AutoCAD layouts, placeholders) are ignored.
    /// </summary>
    public List<(string ColumnId, string Label, bool? State, int Locked)> RevisionStates(IReadOnlyList<DataRowView> views)
    {
        var list = new List<(string, string, bool?, int)>();
        if (_table is null) return list;
        foreach (var c in _table.Columns.Where(c => c.Group == RevisionsGroup))
        {
            string gridColumn = _gridColumnByColumnId[c.Id];
            var values = views.Where(v => RowOf(v)?.Values.ContainsKey(c.Id) == true)
                .Select(v => string.Equals(GridEdits.Value(v.Row, gridColumn), "Yes", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (values.Count == 0) continue;
            bool? state = values.All(x => x) ? true : values.All(x => !x) ? false : null;
            int locked = views.Count(v => RowOf(v)?.Values.ContainsKey(c.Id) == true && EditBlocker(v, gridColumn) is not null);
            list.Add((c.Id, c.Name, state, locked));
        }
        return list;
    }

    /// <summary>How many of the given rows are sheets with revision columns.</summary>
    public int RevisionSheetCount(IReadOnlyList<DataRowView> views) =>
        views.Count(v => RowOf(v)?.Values.Keys.Any(k => _table!.ColumnsById.TryGetValue(k, out var c) && c.Group == RevisionsGroup) == true);

    /// <summary>Shows/hides revisions on the given sheets (staged as normal edits). Returns a status line.</summary>
    public string SetRevisions(IReadOnlyList<DataRowView> views, IReadOnlyDictionary<string, bool> changes)
    {
        int done = 0;
        var skipped = new List<string>();
        foreach (var view in views)
        {
            var row = RowOf(view);
            if (row is null) continue;
            foreach (var (columnId, show) in changes)
            {
                if (!row.Values.ContainsKey(columnId)) continue;
                var reason = TrySetCell(view, _gridColumnByColumnId[columnId], show ? "Yes" : "No");
                if (reason is null) done++;
                else skipped.Add($"{row.Key}: {reason}");
            }
        }
        Status = skipped.Count == 0
            ? $"Updated {done} revision setting(s). Review & apply to write them to Revit."
            : $"Updated {done}; {skipped.Count} could not be changed ({skipped[0]}).";
        return Status;
    }

    /// <summary>Why this cell cannot be edited, or null if it can.</summary>
    public string? EditBlocker(DataRowView view, string gridColumn)
    {
        if (!_gridColumnIds.TryGetValue(gridColumn, out var columnId)) return "File and item names are not editable here.";
        var row = RowOf(view);
        return row is null ? "This row is out of date: reload the data." : Editing.Blocker(row, columnId);
    }

    public bool IsEdited(DataRowView view, string gridColumn, out string? original)
    {
        original = null;
        var row = view.Row;
        if (!_gridColumnIds.ContainsKey(gridColumn)) return false;
        return GridEdits.IsEdited(row, gridColumn, out original);
    }

    /// <summary>Sets a cell if it is editable. Returns null on success, else why not.</summary>
    public string? TrySetCell(DataRowView view, string gridColumn, string? value)
    {
        var blocker = EditBlocker(view, gridColumn);
        if (blocker is not null) return blocker;
        if (Editing.SameValue(view[gridColumn] as string, value)) return null;
        view.BeginEdit();
        view[gridColumn] = value ?? "";
        view.EndEdit();
        return null;
    }

    /// <summary>Same as <see cref="TrySetCell"/> addressed by table row and Nexus column id.</summary>
    public string? TrySetValue(TableRow row, string columnId, string value)
    {
        if (_grid is null || _table is null) return "Nothing is loaded.";
        if (!_gridColumnByColumnId.TryGetValue(columnId, out var gridColumn)) return "That column is not in the loaded data.";
        int index = _table.Rows.IndexOf(row);
        if (index < 0) return "That row is no longer loaded.";
        var view = _grid.DefaultView.Cast<DataRowView>().FirstOrDefault(v => (int)v.Row[RowIndexColumn] == index)
                   ?? new DataView(_grid).Cast<DataRowView>().First(v => (int)v.Row[RowIndexColumn] == index);
        return TrySetCell(view, gridColumn, value);
    }

    /// <summary>Puts cells back to the value read from the model.</summary>
    public int Revert(IEnumerable<(DataRowView View, string Column)> cells)
    {
        int n = 0;
        foreach (var (view, column) in cells)
        {
            if (!IsEdited(view, column, out var original)) continue;
            view.BeginEdit();
            view[column] = original ?? "";
            view.EndEdit();
            n++;
        }
        Status = n == 0 ? "Nothing to revert in the selection." : $"Reverted {n} cell(s).";
        return n;
    }

    /// <summary>A row edit was committed (or cancelled): recount, since the cell event fired while it was still proposed.</summary>
    private void OnGridRowChanged(object sender, DataRowChangeEventArgs e)
    {
        PendingEdits = CollectEdits().Count;
        DataChanged?.Invoke();
    }

    private void OnGridValueChanged(object sender, DataColumnChangeEventArgs e)
    {
        // A sheet field and its source column are the same property: keep both cells in step.
        if (!_syncing && _table is not null && e.Row[RowIndexColumn] is int i && i < _table.Rows.Count
            && e.Column?.ColumnName is { } changedColumn && _gridColumnIds.TryGetValue(changedColumn, out var columnId)
            && _table.Rows[i].Values.TryGetValue(columnId, out var property))
        {
            _syncing = true;
            try
            {
                foreach (var (otherId, otherValue) in _table.Rows[i].Values)
                {
                    if (otherId == columnId || !ReferenceEquals(otherValue, property)) continue;
                    if (_gridColumnByColumnId.TryGetValue(otherId, out var otherColumn) && !Equals(e.Row[otherColumn], e.ProposedValue))
                        e.Row[otherColumn] = e.ProposedValue;
                }
            }
            finally
            {
                _syncing = false;
            }
        }
        PendingEdits = CollectEdits().Count;
        DataChanged?.Invoke();
    }

    // ------------------------------------------------------------------ details pane

    private void BuildDetails()
    {
        foreach (var g in DetailGroups)
            foreach (var r in g.Rows) r.Dispose();
        DetailGroups.Clear();

        var view = _selectedRow;
        var row = RowOf(view);
        if (view is null || row is null || _table is null) return;

        foreach (var g in _table.Columns.Where(c => row.Values.ContainsKey(c.Id)).GroupBy(c => c.Group))
        {
            var group = new DetailGroup(g.Key);
            foreach (var c in g)
                group.Rows.Add(new DetailRow(view, _gridColumnByColumnId[c.Id], c.Group, c.Label, row.Values[c.Id], EditBlocker));
            DetailGroups.Add(group);
        }
    }

    // ------------------------------------------------------------------ show in the program

    public Task ShowInModelAsync(IReadOnlyList<DataRowView> views) =>
        ShowInModelAsync(views.Select(RowOf).OfType<TableRow>().Distinct().ToList());

    public async Task ShowInModelAsync(IReadOnlyList<TableRow> rows)
    {
        if (rows.Count == 0)
        {
            Status = "Select one or more rows first.";
            return;
        }
        foreach (var group in rows.GroupBy(r => (Run: r.Source.Tag as ResultRun, Doc: r.Source.Result.DocumentId)))
        {
            var agent = group.Key.Run?.Agent;
            if (agent is null) continue;
            if (!agent.Connection.CanSelect)
            {
                Status = $"{agent.Title} cannot show items yet (update its Nexus add-in).";
                continue;
            }
            try
            {
                var result = await agent.Connection.SelectAsync(new SelectRequest
                {
                    DocumentId = group.Key.Doc,
                    ItemIds = group.Select(r => r.ItemId).Where(id => id.Length > 0).Distinct().ToList(),
                });
                Status = result.Message;
            }
            catch (AgentRequestException ex)
            {
                Status = $"{agent.Title}: {ex.Error.Message}";
            }
        }
    }

    // ------------------------------------------------------------------ edits

    private List<CellEdit> CollectEdits()
    {
        var edits = new List<CellEdit>();
        if (_grid is null || _table is null) return edits;
        foreach (DataRow row in _grid.Rows)
        {
            if (!GridEdits.MayBeEdited(row)) continue;
            var tableRow = _table.Rows[(int)row[RowIndexColumn]];
            foreach (var (gridColumn, columnId) in _gridColumnIds)
            {
                if (!GridEdits.IsEdited(row, gridColumn, out _)) continue;
                string? now = GridEdits.Value(row, gridColumn);
                if (!tableRow.Values.ContainsKey(columnId)) continue;
                edits.Add(new CellEdit(tableRow, columnId, now ?? ""));
            }
        }
        return edits;
    }

    private void DiscardEdits()
    {
        CommitGridEdits?.Invoke();
        _grid?.RejectChanges();
        PendingEdits = 0;
        Status = "Changes discarded.";
    }

    /// <summary>Asks before throwing away unapplied edits. True when there are none or the user agrees.</summary>
    public bool ConfirmDiscardEdits()
    {
        CommitGridEdits?.Invoke();
        if (_pendingEdits == 0) return true;
        if (!Dialogs.Confirm("Discard your changes?",
                $"{PendingText}. Switching now discards {(_pendingEdits == 1 ? "it" : "them")}. To keep {(_pendingEdits == 1 ? "it" : "them")}, use Review & apply first.",
                "Discard", "Keep editing"))
            return false;
        DiscardEdits();
        return true;
    }

    public bool ConfirmExit()
    {
        CommitGridEdits?.Invoke();
        if (_pendingEdits == 0) return true;
        return Dialogs.Confirm("Exit Nexus?",
            $"{PendingText}. Exiting discards {(_pendingEdits == 1 ? "it" : "them")}.", "Exit", "Keep editing");
    }

    private async Task ReviewAndApplyAsync()
    {
        CommitGridEdits?.Invoke();
        var edits = CollectEdits();
        PendingEdits = edits.Count;
        if (edits.Count == 0) return;

        var review = new ApplyChangesWindow(new ApplyChangesViewModel(edits, ApplyAsync)) { Owner = Application.Current.MainWindow };
        review.ShowDialog();

        var changed = review.ViewModel.ChangedRuns;
        if (changed.Count == 0) return;
        // Reading again rebuilds the table: keep the changes that were left unticked pending.
        var keep = review.ViewModel.NotApplied
            .Select(c => (Doc: c.Edit.Row.Source.Result.DocumentId, c.Edit.Row.ItemId, c.Edit.ColumnId, c.Edit.NewValue)).ToList();
        await RereadAsync(changed);
        RestageEdits(keep);
    }

    /// <summary>Stages values again after the table was rebuilt (rows found by file and item id).</summary>
    private void RestageEdits(IReadOnlyList<(string Doc, string ItemId, string ColumnId, string Value)> edits)
    {
        if (edits.Count == 0 || _table is null) return;
        int kept = 0;
        foreach (var (doc, itemId, columnId, value) in edits)
        {
            var row = _table.Rows.FirstOrDefault(r => r.ItemId == itemId && r.Source.Result.DocumentId == doc);
            if (row is not null && TrySetValue(row, columnId, value) is null) kept++;
        }
        Status = $"Applied. {kept} unticked change{(kept == 1 ? "" : "s")} still pending.";
    }

    /// <summary>Sends the edits, one request per program file, and fills in each row's outcome.</summary>
    private async Task<List<ResultRun>> ApplyAsync(IReadOnlyList<ChangeRow> rows)
    {
        _busy = true;
        var changedRuns = new List<ResultRun>();
        // History: the sheet set as it was, so "what changed since" can always go back to before this apply.
        if (CaptureSnapshot($"Before applying {rows.Count} change{(rows.Count == 1 ? "" : "s")}", SheetSnapshot.KindBeforeApply) is { } before)
        {
            try { SnapshotStore.Save(before); }
            catch (Exception ex) { HubLog.Warn("Could not save the before-apply snapshot.", ex); }
        }
        var plans = Editing.Plan(rows.Select(r => r.Edit));
        var work = BeginWork("Applying changes", plans.Count);
        try
        {
            var byEdit = rows.ToDictionary(r => r.Edit);
            foreach (var (pid, request, edits) in plans)
            {
                var planned = edits.Select(e => byEdit[e]).ToList();
                // Cancel stops before the next file; a file being written always finishes (one undo step each).
                if (work.IsCancellationRequested)
                {
                    foreach (var r in planned) r.SetOutcome("Skipped", "Cancelled before this file was updated.", null);
                    continue;
                }
                foreach (var r in rows.Where(r => r.Status == "" && r.Edit.ProcessId == pid && r.Edit.DocumentId == request.DocumentId && !planned.Contains(r)))
                    r.SetOutcome("Skipped", "The same property is changed in another column or row; that change is used.", null);

                var agent = (edits[0].Row.Source.Tag as ResultRun)?.Agent;
                if (agent is null)
                {
                    foreach (var r in planned) r.SetOutcome("Failed", "The program for this file is no longer connected.", null);
                    continue;
                }

                Status = $"Applying {request.Changes.Count} change(s) to {edits[0].Row.Document}…";
                try
                {
                    var result = await agent.Connection.WriteAsync(request);
                    foreach (var cr in result.Results)
                        if (cr.Index >= 0 && cr.Index < planned.Count)
                            planned[cr.Index].SetOutcome(cr.Status.ToString(), cr.Message, cr.NewValue);
                    foreach (var w in result.Warnings) HubLog.Warn($"{result.DocumentTitle}: {w}");
                    HubLog.Info($"{result.DocumentTitle}: {(result.Committed ? "committed" : "nothing committed")} " +
                                $"({result.Results.Count(c => c.Status == ChangeStatus.Applied)} applied) in {result.ElapsedMs} ms");
                    if (result.Committed)
                        changedRuns.AddRange(Runs.Where(x => x.Agent == agent && x.Result?.DocumentId == request.DocumentId));
                    if (result.Warnings.Count > 0)
                        foreach (var r in planned.Where(r => r.Status == "Applied"))
                            r.Message = "Warning: " + string.Join("; ", result.Warnings);
                }
                catch (AgentRequestException ex)
                {
                    foreach (var r in planned) r.SetOutcome("Failed", ex.Error.Message, null);
                    HubLog.Warn($"Write to {edits[0].Row.Document} failed: {ex.Error}");
                }
                StepWork(work);
            }
        }
        finally
        {
            EndWork(work);
            _busy = false;
            ChangeLog.Append(rows.Where(r => r.Status == "Applied").Select(r => new ChangeLogEntry
            {
                TimeUtc = DateTime.UtcNow,
                User = Environment.UserName,
                Program = r.Edit.Row.Source.Host.Name,
                File = r.Document,
                Item = r.Item,
                Property = r.Property,
                Before = r.OldValue,
                After = r.Result ?? r.NewValue,
                Status = r.Status,
            }));
        }
        Status = $"{rows.Count(r => r.Status == "Applied")} of {rows.Count} change(s) applied.";
        return changedRuns.Distinct().ToList();
    }

    /// <summary>The sheet set as read from the files (Sheets view only; null otherwise).</summary>
    public SheetSnapshot? CaptureSnapshot(string name, string kind) =>
        _dataset is { IsSheetIndex: true } && _table is not null ? SheetSnapshot.Capture(_table.Rows, name, kind) : null;

    public bool IsSheetsView => _dataset is { IsSheetIndex: true };

    /// <summary>Reads changed files again so the grid shows what the program now has.</summary>
    private async Task RereadAsync(IReadOnlyList<ResultRun> runs)
    {
        if (_dataset is null) return;
        _busy = true;
        try
        {
            var updated = Runs.ToList();
            foreach (var old in runs)
            {
                if (old.Agent is null || old.Request is null) continue;
                Status = $"Refreshing {old.DocumentTitle}…";
                var fresh = await ReadOneAsync(old.Agent, old.DocumentTitle, old.Request);
                int index = updated.IndexOf(old);
                if (index >= 0) updated[index] = fresh;
            }
            Runs = updated;
        }
        finally
        {
            _busy = false;
        }
        ShowResults(_dataset);
        Status = "Applied and refreshed from the model. Use Undo in Revit (or U in AutoCAD) to revert.";
    }

    // ------------------------------------------------------------------ Excel (see MainViewModel.Excel.cs)

    private Task ExportExcel()
    {
        if (_grid is null || _table is null) return Task.CompletedTask;
        CommitGridEdits?.Invoke();
        Directory.CreateDirectory(NexusPaths.ExportsDir);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = NexusPaths.ExportsDir,
            FileName = $"nexus-{_dataset?.Title.ToLowerInvariant().Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;

        var specs = ColumnSpecs(_dataset);
        var headers = specs.Select(s => s.Group.Length == 0 || s.Group == SheetFieldMap.Group ? s.Name : $"{s.Group} › {s.Name}").ToList();
        var rows = _grid.DefaultView.Cast<DataRowView>().Select(v => (IReadOnlyList<string?>)specs.Select(s => (v[s.Column] as string)?.TrimStart()).ToList());
        try
        {
            ExcelWorkbook.Export(dialog.FileName, headers, rows, _dataset?.Title ?? "Nexus");
            Status = "Exported " + dialog.FileName;
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HubLog.Error("Excel export failed", ex);
            Notify(NoticeKind.Error, "Export to Excel failed", ex.Message);
        }
        return Task.CompletedTask;
    }

    private Task ExportLegacy(string kind)
    {
        if (_table is null) return Task.CompletedTask;
        Directory.CreateDirectory(NexusPaths.ExportsDir);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = NexusPaths.ExportsDir,
            FileName = $"nexus-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}",
            Filter = kind == "json" ? "JSON (*.json)|*.json" : "CSV for Excel (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try
        {
            switch (kind)
            {
                case "wide": Exporters.WriteWideCsv(_table, _visible, dialog.FileName); break;
                case "long": Exporters.WriteLongCsv(_table, null, dialog.FileName); break;
                default: Exporters.WriteJson(Runs.Select(r => r.ToSource()).OfType<ResultSource>().ToList(), dialog.FileName); break;
            }
            Status = "Exported " + dialog.FileName;
        }
        catch (Exception ex)
        {
            HubLog.Error("Export failed", ex);
            Notify(NoticeKind.Error, "Export failed", ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// What can be edited in the loaded data, per program and column group, and why the rest is locked.
    /// </summary>
    public IEnumerable<string> EditingReport()
    {
        if (_table is null || _table.Rows.Count == 0)
        {
            yield return "Nothing is loaded.";
            yield break;
        }
        foreach (var host in _table.Rows.GroupBy(r => r.Host))
        {
            var cells = host.SelectMany(r => r.Values
                    .Where(v => !v.Key.StartsWith(SheetFieldMap.Group + " ›", StringComparison.Ordinal))
                    .Select(v => (Group: _table.ColumnsById.TryGetValue(v.Key, out var k) ? k.Group : "", Blocker: Editing.Blocker(r, v.Key))))
                .ToList();
            var features = host.First().Source.Host.Features;
            yield return $"{host.Key}: {cells.Count(c => c.Blocker is null)} editable, {cells.Count(c => c.Blocker is not null)} locked " +
                         $"(add-in features: {(features.Count == 0 ? "none - update the add-in" : string.Join(", ", features))})";
            foreach (var g in cells.GroupBy(c => c.Group).OrderBy(g => g.Key))
            {
                int editable = g.Count(c => c.Blocker is null);
                var reasons = g.Where(c => c.Blocker is not null).GroupBy(c => c.Blocker!).OrderByDescending(r => r.Count())
                    .Take(3).Select(r => $"{r.Count()} × {r.Key}");
                yield return $"    {g.Key}: {editable} editable" + (editable == g.Count() ? "" : "; locked: " + string.Join("; ", reasons));
            }
        }
    }

    /// <summary>Errors and warnings of the last load, for the Messages window.</summary>
    public IEnumerable<string> IssueLines() =>
        Runs.SelectMany(r => r.Failed
            ? new[] { $"{r.DocumentTitle}: {r.Error!.Code}: {r.Error.Message}" }
            : r.Result!.Warnings.Select(w => $"{r.DocumentTitle}: {w}"));
}
