using System.Windows;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>The deliverables list next to the models; ticked differences are pushed from the list into the models.</summary>
public partial class DeliverablesWindow : Window
{
    public DeliverablesWindow(DeliverablesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Space ticks or unticks the selected rows (like a list of check boxes).</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var rows = Grid.SelectedItems.OfType<DeliverableRow>().Where(r => r.CanPush).ToList();
        if (rows.Count == 0) return;
        bool push = !rows.All(r => r.Push);
        foreach (var r in rows) r.Push = push;
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
