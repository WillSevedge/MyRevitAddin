using System.Windows;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Every open DWG next to its dummy sheet in the Revit index model; ticked rows are synced.</summary>
public partial class SheetIndexWindow : Window
{
    public SheetIndexWindow(SheetIndexViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Space ticks or unticks the selected rows.</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var rows = Grid.SelectedItems.OfType<IndexRow>().Where(r => r.CanPush).ToList();
        if (rows.Count == 0) return;
        bool push = !rows.All(r => r.Push);
        foreach (var r in rows) r.Push = push;
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
