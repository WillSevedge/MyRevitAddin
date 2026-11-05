namespace Nexus.Hub.ViewModels;

/// <summary>The hub's workspaces (navigation rail).</summary>
public static class Workspaces
{
    public const string Home = "Home";
    public const string Data = "Data";
    public const string Connections = "Connections";
}

/// <summary>
/// Shell state: which workspace shows, the navigation rail, table density, the inspector pin, the selection count,
/// the table's empty / loading / error states and the connection summary. Saved choices go to the hub settings.
/// </summary>
public sealed partial class MainViewModel
{
    private string _workspace = Workspaces.Data;

    public string Workspace
    {
        get => _workspace;
        set
        {
            if (!Set(ref _workspace, value)) return;
            Raise(nameof(IsHome));
            Raise(nameof(IsData));
            Raise(nameof(IsConnections));
            Raise(nameof(WorkspaceTitle));
            _settings.Workspace = value;
            _settings.Save();
        }
    }

    public bool IsHome { get => _workspace == Workspaces.Home; set { if (value) Workspace = Workspaces.Home; } }
    public bool IsData { get => _workspace == Workspaces.Data; set { if (value) Workspace = Workspaces.Data; } }
    public bool IsConnections { get => _workspace == Workspaces.Connections; set { if (value) Workspace = Workspaces.Connections; } }

    /// <summary>The navigation rail shows labels (wide) or icons only.</summary>
    public bool IsRailExpanded
    {
        get => _settings.RailExpanded;
        set
        {
            if (_settings.RailExpanded == value) return;
            _settings.RailExpanded = value;
            _settings.Save();
            Raise();
        }
    }

    /// <summary>Compact table rows (more on screen) instead of comfortable ones.</summary>
    public bool IsCompact
    {
        get => _settings.Density == "Compact";
        set
        {
            if (IsCompact == value) return;
            _settings.Density = value ? "Compact" : "Comfortable";
            _settings.Save();
            Raise();
        }
    }

    /// <summary>The inspector stays open, also after a restart. Pinning opens it.</summary>
    public bool IsInspectorPinned
    {
        get => _settings.InspectorPinned;
        set
        {
            if (_settings.InspectorPinned == value) return;
            _settings.InspectorPinned = value;
            _settings.Save();
            Raise();
            if (value) ShowDetails = true;
        }
    }

    // ------------------------------------------------------------------ selection

    private int _selectionCount;

    /// <summary>Rows selected in the table (set by the window).</summary>
    public int SelectionCount
    {
        get => _selectionCount;
        set
        {
            if (!Set(ref _selectionCount, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectionText));
        }
    }

    public bool HasSelection => _selectionCount > 0;
    public string SelectionText => _selectionCount == 1 ? "1 selected" : $"{_selectionCount} selected";

    // ------------------------------------------------------------------ table states

    /// <summary>Reading, and nothing to show yet: a loading state instead of an empty table.</summary>
    public bool IsLoading => _busy && (_table is null || _table.Rows.Count == 0);

    /// <summary>Every file failed to read: an error state with Try again.</summary>
    public bool IsReadError => IsEmpty && Runs.Count > 0 && Runs.All(r => r.Failed);

    /// <summary>Empty, but not because of an error.</summary>
    public bool IsPlainEmpty => IsEmpty && !IsReadError;

    /// <summary>Icon of the empty state: a file when none is ticked, else the view's own icon.</summary>
    public string EmptyGlyph => Agents.Count > 0 && Runs.Count == 0 ? "" : _dataset?.Icon ?? "";

    /// <summary>"Sheets · 3 files" for the window header breadcrumb.</summary>
    public string WorkspaceTitle => _workspace switch
    {
        Workspaces.Home => "Home",
        Workspaces.Connections => "Connections",
        _ => _dataset?.Title ?? "Data",
    };

    private void RaiseEmptyState()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(IsLoading));
        Raise(nameof(IsReadError));
        Raise(nameof(IsPlainEmpty));
        Raise(nameof(EmptyGlyph));
    }

    // ------------------------------------------------------------------ connections

    /// <summary>"2 programs · 5 files", for the status bar.</summary>
    public string ConnectionSummary
    {
        get
        {
            if (Agents.Count == 0) return "No programs connected";
            int files = Agents.Sum(a => a.Documents.Count);
            return $"{Agents.Count} program{(Agents.Count == 1 ? "" : "s")} · {files} file{(files == 1 ? "" : "s")}";
        }
    }

    /// <summary>The worst state among the programs (status bar dot): Error, Busy, Connecting, Connected or Disconnected (none).</summary>
    public ConnectionState OverallConnection =>
        Agents.Count == 0 ? ConnectionState.Disconnected
        : Agents.Any(a => a.State is ConnectionState.Error or ConnectionState.Disconnected) ? ConnectionState.Error
        : Agents.Any(a => a.State == ConnectionState.Busy) ? ConnectionState.Busy
        : Agents.Any(a => a.State == ConnectionState.Connecting) ? ConnectionState.Connecting
        : ConnectionState.Connected;

    public int ProgramCount => Agents.Count;
    public int OpenFileCount => Agents.Sum(a => a.Documents.Count);
    public int TickedFileCount => Agents.Sum(a => a.Documents.Count(d => d.IsChecked));

    private void RaiseConnections()
    {
        Raise(nameof(ConnectionSummary));
        Raise(nameof(OverallConnection));
        Raise(nameof(ProgramCount));
        Raise(nameof(OpenFileCount));
        Raise(nameof(TickedFileCount));
    }

    /// <summary>
    /// Switches to the Sheets view (the sheet tools work on it) and waits until the sheets are read. False when there
    /// is no Sheets view yet or the user kept unapplied edits.
    /// </summary>
    internal async Task<bool> ShowSheetsAsync()
    {
        IsData = true;
        if (IsSheetsView) return true;
        var sheets = Datasets.FirstOrDefault(d => d.IsSheetIndex);
        if (sheets is null || !ConfirmDiscardEdits()) return false;
        _dataset = sheets;
        Raise(nameof(SelectedDataset));
        Raise(nameof(WorkspaceTitle));
        Raise(nameof(DatasetOptions));
        Raise(nameof(HasOptions));
        _settings.LastDataset = sheets.Id;
        _settings.Save();
        await LoadAsync(confirm: false);
        return IsSheetsView;
    }
}
