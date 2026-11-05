using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

/// <summary>A connected program (one Revit/AutoCAD/Civil 3D/Plant 3D process) in the left panel.</summary>
public sealed class AgentNode : Observable
{
    private string? _error;

    public AgentNode(AgentConnection connection) => Connection = connection;

    public AgentConnection Connection { get; }
    public ObservableCollection<DocumentNode> Documents { get; } = new();

    /// <summary>1, 2... when the same program is open more than once (set by the view model).</summary>
    public int Instance { get; set; } = 1;
    public bool HasSiblings { get; set; }

    /// <summary>"Revit 2026", or "Revit 2026 #2" for a second session.</summary>
    public string Title => Connection.Host.Name + (HasSiblings ? $"  #{Instance}" : "");

    public string Subtitle
    {
        get
        {
            int n = Documents.Count;
            return n == 0 ? "No files open" : n == 1 ? "1 file open" : $"{n} files open";
        }
    }

    private System.Windows.Media.ImageSource? _icon;
    private string? _iconFor;

    /// <summary>The Autodesk product's own icon, read from the installed program (null if not found).</summary>
    public System.Windows.Media.ImageSource? ProductIcon
    {
        get
        {
            string key = $"{Product} {Connection.Host.Version}";
            if (_iconFor != key)
            {
                _iconFor = key;
                _icon = ProductIcons.For(Connection.Host, Product);
            }
            return _icon;
        }
    }

    public bool HasProductIcon => ProductIcon is not null;
    public bool ShowBadge => !HasProductIcon;

    /// <summary>Shown under the program when its add-in cannot take edits (built before editing existed).</summary>
    public string? EditNote => Connection.Client is null || Connection.CanWrite ? null
        : "View only: this program's Nexus add-in is out of date. Close it, Rebuild Solution in Visual Studio, and reopen it.";

    /// <summary>Fallback badge when the product icon cannot be read.</summary>
    public string Badge => Product switch
    {
        "Revit" => "R",
        "Civil 3D" => "C3D",
        "Plant 3D" => "P3D",
        _ => "A",
    };

    public string BadgeColor => Product switch
    {
        "Revit" => "#1F72C7",
        "Civil 3D" => "#0F8E8C",
        "Plant 3D" => "#6A4C93",
        _ => "#C8102E",
    };

    private string Product => Products.Of(Connection.Host);

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) Raise(nameof(IsBusy));
        }
    }

    /// <summary>The program is busy (not an error): the note is shown in grey and the hub retries.</summary>
    public bool IsBusy => Connection.IsBusy;

    /// <summary>Busy (opening a model…), connecting (first contact), not reachable, error, or connected.</summary>
    public ConnectionState State =>
        Connection.IsBusy ? ConnectionState.Busy
        : Connection.Client is null ? (_error is null ? ConnectionState.Connecting : ConnectionState.Disconnected)
        : _error is not null ? ConnectionState.Error
        : ConnectionState.Connected;

    public string StateText => State switch
    {
        ConnectionState.Busy => "Busy",
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Error => "Error",
        _ => "Connected",
    };

    /// <summary>"Version 2026 · process 1234".</summary>
    public string HostDetails => $"Version {Connection.Host.Version} · process {Connection.Host.ProcessId}";

    /// <summary>What the add-in can do: "Read · Edit · PDF · Show in model · Create sheets".</summary>
    public string Capabilities
    {
        get
        {
            var can = new List<string> { "Read" };
            if (Connection.CanWrite) can.Add("Edit");
            if (Connection.CanExportPdf) can.Add("PDF");
            if (Connection.CanSelect) can.Add("Show in model");
            if (Connection.CanCreateSheets) can.Add("Create sheets");
            return string.Join(" · ", can);
        }
    }

    public int TickedCount => Documents.Count(d => d.IsChecked);

    public void Refreshed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(Badge));
        Raise(nameof(BadgeColor));
        Raise(nameof(ProductIcon));
        Raise(nameof(HasProductIcon));
        Raise(nameof(ShowBadge));
        Raise(nameof(EditNote));
        Error = Connection.LastError;
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(HostDetails));
        Raise(nameof(Capabilities));
        Raise(nameof(TickedCount));
    }
}

/// <summary>How a program's connection stands (Connections workspace, status bar).</summary>
public enum ConnectionState { Connected, Connecting, Busy, Disconnected, Error }

/// <summary>An open file; ticked files are loaded into the grid.</summary>
public sealed class DocumentNode : Observable
{
    private bool _isChecked;

    public DocumentNode(AgentNode agent, DocumentInfo info)
    {
        Agent = agent;
        Info = info;
    }

    public AgentNode Agent { get; }
    public DocumentInfo Info { get; set; }

    /// <summary>Raised when the user ticks or unticks the file.</summary>
    public event Action<DocumentNode>? CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke(this);
        }
    }

    /// <summary>Set without raising <see cref="CheckedChanged"/> (when restoring state).</summary>
    public void Restore(bool isChecked) => Set(ref _isChecked, isChecked, nameof(IsChecked));

    public string Title => Info.Title;

    public string Flags
    {
        get
        {
            var flags = new List<string>();
            if (Info.IsActive) flags.Add("active");
            if (Info.IsReadOnly) flags.Add("read-only");
            if (Info.IsModified) flags.Add("unsaved");
            foreach (var (k, v) in Info.Extra)
                if (v == "True") flags.Add(k.Replace("Is", "").ToLowerInvariant());
            return string.Join(" · ", flags);
        }
    }

    public string ToolTip => Info.Path ?? Info.Title;
}

/// <summary>One reader of one program, with its options (shown under Options for the dataset).</summary>
public sealed class ReaderNode
{
    public ReaderNode(ReaderDescriptor descriptor, string hostKind, string hostLabel)
    {
        Descriptor = descriptor;
        HostKind = hostKind;
        HostLabel = hostLabel;
        foreach (var o in descriptor.Options) Options.Add(new OptionNode(o));
    }

    public ReaderDescriptor Descriptor { get; }
    public string HostKind { get; }
    public string HostLabel { get; }
    public ObservableCollection<OptionNode> Options { get; } = new();
    public string Title => $"{HostLabel}: {Products.TitleCase(Descriptor.DisplayName)}";

    public Dictionary<string, string> OptionValues() =>
        Options.Where(o => o.Value is not null).ToDictionary(o => o.Option.Name, o => o.Value!);
}

public sealed class OptionNode : Observable
{
    private string? _value;

    public OptionNode(ReaderOption option)
    {
        Option = option;
        _value = option.Default;
    }

    public ReaderOption Option { get; }
    public bool IsBool => Option.Type == "bool";
    public bool IsText => !IsBool;

    public string? Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) Raise(nameof(BoolValue));
        }
    }

    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}

/// <summary>Something to look at: the cross-program sheet index, or one reader's data.</summary>
public sealed class DatasetNode
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>"Shared" (every program), "Revit", "AutoCAD"... (grouping in the sidebar).</summary>
    public string Category { get; init; } = "";
    /// <summary>Adds the standard Sheet › … columns.</summary>
    public bool IsSheetIndex { get; init; }
    /// <summary>Host kind → the reader that supplies this dataset there.</summary>
    public Dictionary<string, ReaderNode> Readers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Programs => string.Join(", ", Readers.Values.Select(r => r.HostLabel).Distinct());

    /// <summary>Segoe Fluent Icons glyph for the navigation list.</summary>
    public string Icon => IsSheetIndex ? "\uE7C3"
        : Id.EndsWith("projectinfo", StringComparison.Ordinal) ? "\uE946"
        : Id.EndsWith("revisions", StringComparison.Ordinal) ? "\uE81C"
        : Id.EndsWith("layouts", StringComparison.Ordinal) ? "\uE8A1"
        : Id.Contains("civil", StringComparison.OrdinalIgnoreCase) ? "\uE909"
        : Id.Contains("fabrication.database", StringComparison.OrdinalIgnoreCase) ? "\uE8F1"
        : Id.Contains("fabrication", StringComparison.OrdinalIgnoreCase) ? "\uE90F"
        : Id.Contains("plant.project", StringComparison.OrdinalIgnoreCase) ? "\uE8B7"
        : Id.Contains("plant", StringComparison.OrdinalIgnoreCase) ? "\uE7B8"
        : "\uE8FD";
}

/// <summary>One read of one file (kept for re-reading after edits and for warnings).</summary>
public sealed class ResultRun
{
    public required HostInfo Host { get; init; }
    public required string DocumentTitle { get; init; }
    public required string ReaderId { get; init; }
    public AgentNode? Agent { get; init; }
    public ReadRequest? Request { get; init; }
    public ReadResult? Result { get; init; }
    public ErrorInfo? Error { get; init; }

    public string Title => $"{DocumentTitle} · {ReaderId}";
    public bool Failed => Error is not null;

    public ResultSource? ToSource() => Result is null ? null : new ResultSource
    {
        Host = Host,
        DocumentTitle = DocumentTitle,
        Result = Result,
        Tag = this,
    };
}

/// <summary>Column chooser: a group of columns.</summary>
public sealed class ColumnGroupNode : Observable
{
    private bool? _isChecked = false;
    private bool _updating;

    public ColumnGroupNode(string name) => Name = name;

    public string Name { get; }
    public ObservableCollection<ColumnNode> Columns { get; } = new();
    public string Title => $"{Name}  ({Columns.Count})";

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value) || value is null) return;
            _updating = true;
            foreach (var c in Columns) c.IsChecked = value.Value;
            _updating = false;
        }
    }

    internal void ChildChanged()
    {
        if (_updating) return;
        bool all = Columns.All(c => c.IsChecked), none = Columns.All(c => !c.IsChecked);
        Set(ref _isChecked, all ? true : none ? false : null, nameof(IsChecked));
    }
}

public sealed class ColumnNode : Observable
{
    private bool _isChecked;

    public ColumnNode(ColumnGroupNode group, ColumnKey key)
    {
        Group = group;
        Key = key;
    }

    public ColumnGroupNode Group { get; }
    public ColumnKey Key { get; }
    public string Name => Key.Label;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) Group.ChildChanged();
        }
    }
}

/// <summary>Details pane: one property of the selected row, editable in place.</summary>
public sealed class DetailRow : Observable, IDisposable
{
    private readonly DataRowView _view;
    private readonly Func<DataRowView, string, string?> _blocker;

    public DetailRow(DataRowView view, string gridColumn, string group, string name, PropertyValue property,
        Func<DataRowView, string, string?> blocker)
    {
        _view = view;
        _blocker = blocker;
        GridColumn = gridColumn;
        Group = group;
        Name = name;
        Property = property;
        ((INotifyPropertyChanged)_view).PropertyChanged += OnViewChanged;
    }

    public string GridColumn { get; }
    public string Group { get; }
    public string Name { get; }
    public PropertyValue Property { get; }

    public string? Reason => _blocker(_view, GridColumn);
    public bool IsEditable => Reason is null;
    public bool IsReadOnly => !IsEditable;

    public bool IsEdited
    {
        get
        {
            return GridEdits.IsEdited(_view.Row, GridColumn, out _);
        }
    }

    public string ToolTip => IsEdited
        ? $"Edited. Was: {(GridEdits.IsEdited(_view.Row, GridColumn, out var was) ? was : "")}"
        : Reason is { } r ? "Not editable: " + r
        : $"{Property.Source}{(Property.DataType is null ? "" : " · " + Property.DataType)}{(Property.Units is null ? "" : " · " + Property.Units)}";

    public string? Value
    {
        get => _view[GridColumn] as string;
        set
        {
            if (!IsEditable || Editing.SameValue(value, Value)) return;
            _view.BeginEdit();
            _view[GridColumn] = value ?? "";
            _view.EndEdit();
        }
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != GridColumn && !string.IsNullOrEmpty(e.PropertyName)) return;
        Raise(nameof(Value));
        Raise(nameof(IsEdited));
        Raise(nameof(ToolTip));
    }

    public void Dispose() => ((INotifyPropertyChanged)_view).PropertyChanged -= OnViewChanged;
}

/// <summary>A titled list of <see cref="DetailRow"/>s.</summary>
public sealed class DetailGroup
{
    public DetailGroup(string name) => Name = name;
    public string Name { get; }
    public List<DetailRow> Rows { get; } = new();
}
