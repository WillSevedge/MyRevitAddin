using System.Windows;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Shows pending edits (old → new), applies them, and shows each outcome.</summary>
public partial class ApplyChangesWindow : Window
{
    public ApplyChangesWindow(ApplyChangesViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closing += (_, e) => e.Cancel = viewModel.IsApplying;
        viewModel.Succeeded += Close;
    }

    public ApplyChangesViewModel ViewModel { get; }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
