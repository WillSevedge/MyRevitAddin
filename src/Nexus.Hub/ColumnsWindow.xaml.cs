using System.Windows;
using System.Windows.Controls;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Column chooser: which properties are columns in the grid.</summary>
public partial class ColumnsWindow : Window
{
    private readonly List<ColumnGroupNode> _groups;

    public ColumnsWindow(List<ColumnGroupNode> groups)
    {
        _groups = groups;
        InitializeComponent();
        Tree.ItemsSource = groups;
        Loaded += (_, _) => FilterBox.Focus();
    }

    public IEnumerable<string> CheckedColumnIds =>
        _groups.SelectMany(g => g.Columns).Where(c => c.IsChecked).Select(c => c.Key.Id);

    private IEnumerable<ColumnGroupNode> Shown =>
        string.IsNullOrWhiteSpace(FilterBox.Text)
            ? _groups
            : _groups.Where(g => g.Name.Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase)
                                 || g.Columns.Any(c => c.Name.Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase)));

    private void OnFilter(object sender, TextChangedEventArgs e) => Tree.ItemsSource = Shown.ToList();

    private void OnAll(object sender, RoutedEventArgs e)
    {
        foreach (var g in Shown) g.IsChecked = true;
    }

    private void OnNone(object sender, RoutedEventArgs e)
    {
        foreach (var g in Shown) g.IsChecked = false;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
