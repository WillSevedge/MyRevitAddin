using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nexus.Rename;

namespace Nexus.Agent.Revit.Rename;

internal abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One kind in "What to rename" (Views, Sheet Numbers, Levels...).</summary>
internal sealed class TargetRow : Notifier
{
    private bool _isChecked;

    public TargetRow(RenameTarget target, int count)
    {
        Target = target;
        Count = count;
    }

    public RenameTarget Target { get; }
    public string Group => Target.Group;
    public string Label => Target.Label;
    public string Tip => Target.Tip;
    public int Count { get; }
    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public event Action? CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke();
        }
    }
}

/// <summary>One line of the preview: current name → new name, and what will happen.</summary>
internal sealed class PreviewRow : Notifier
{
    private bool _include = true;
    private string _new;
    private string _status = "";
    private string _tone = "";
    private string? _message;

    public PreviewRow(RenameItem item, bool inScope)
    {
        Item = item;
        InScope = inScope;
        _include = inScope;
        _new = item.Current;
    }

    public RenameItem Item { get; }
    /// <summary>False when only the Revit selection is renamed and this item is not selected.</summary>
    public bool InScope { get; }
    public string Current => Item.Current;
    public string Kind => Item.Kind;
    public string Detail => Item.Detail;

    public event Action? IncludeChanged;

    public bool Include
    {
        get => _include;
        set
        {
            if (Set(ref _include, value)) IncludeChanged?.Invoke();
        }
    }

    public string New { get => _new; private set => Set(ref _new, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>"Good", "Bad", "Warn" or "" (colour of the status).</summary>
    public string Tone { get => _tone; private set => Set(ref _tone, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool Changes { get; private set; }

    public void Show(RenameResult result)
    {
        New = result.Status is RenameStatus.Skipped ? "" : result.New;
        Changes = result.Status != RenameStatus.Unchanged && result.Status != RenameStatus.Skipped;
        (Status, Tone) = result.Status switch
        {
            RenameStatus.Rename => (result.Message is null ? "Will rename" : "Will rename · " + result.Message, result.Message is null ? "Good" : "Warn"),
            RenameStatus.Unchanged => ("No change", ""),
            RenameStatus.Skipped => ("Not included", ""),
            RenameStatus.Duplicate => ("Name already used", "Bad"),
            RenameStatus.Invalid => ("Not allowed", "Bad"),
            RenameStatus.Empty => ("Empty name", "Bad"),
            RenameStatus.Locked => ("Locked", "Warn"),
            _ => (result.Status.ToString(), ""),
        };
        Message = result.Message;
    }
}
