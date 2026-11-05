using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Nexus.Hub.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool>? _canRun;
    private bool _busy;

    public RelayCommand(Func<Task> run, Func<bool>? canRun = null)
    {
        _run = run;
        _canRun = canRun;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_busy && (_canRun?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _run(); }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
