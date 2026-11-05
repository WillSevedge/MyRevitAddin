using System.Windows;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Snapshots, what changed since, and the change log (see <see cref="HistoryViewModel"/>).</summary>
public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
