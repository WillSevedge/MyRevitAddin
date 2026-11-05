using System.Windows;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Ctrl+K command palette: a search box over actions, views and sheets. Closes when it loses focus.</summary>
public partial class PaletteWindow : Window
{
    private readonly PaletteViewModel _vm;
    private bool _closing;

    public PaletteWindow(PaletteViewModel vm, Window owner)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Owner = owner;
        // Near the top of the hub, centred.
        Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
        Top = owner.Top + 90;
        Loaded += (_, _) => QueryBox.Focus();
        Deactivated += (_, _) => SafeClose();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.Move(1); List.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.Up: _vm.Move(-1); List.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.PageDown: _vm.Move(8); List.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.PageUp: _vm.Move(-8); List.ScrollIntoView(_vm.Selected); e.Handled = true; break;
            case Key.Escape: SafeClose(); e.Handled = true; break;
            case Key.Enter:
                Run(_vm.Selected, alternate: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e) => Run(_vm.Selected, alternate: false);

    /// <summary>Closes first, then runs (the action may open its own window).</summary>
    private void Run(PaletteItem? item, bool alternate)
    {
        if (item is null) return;
        SafeClose();
        var action = alternate && item.RunAlternate is not null ? item.RunAlternate : item.Run;
        Owner?.Dispatcher.BeginInvoke(action);
    }
}
