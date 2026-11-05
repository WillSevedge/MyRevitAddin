using System.Windows;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Markups from Bluebeam (PDF annotations), grouped by sheet.</summary>
public partial class MarkupsWindow : Window
{
    private readonly MarkupsViewModel _vm;

    public MarkupsWindow(MarkupsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.SelectedRows = () => Grid.SelectedItems.OfType<MarkupRow>().ToList();
        Closed += (_, _) => vm.Dispose();
        Loaded += (_, _) => vm.Start();
    }

    /// <summary>Double-click: open the markup's sheet in Revit or AutoCAD.</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ShowInModelCommand.CanExecute(null)) _vm.ShowInModelCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
