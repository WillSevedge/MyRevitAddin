using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Nexus.Agent;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace Nexus.Agent.Revit.Fabrication;

/// <summary>
/// Browses the fabrication database loaded in the model (services, materials, specifications, custom data...)
/// with the bridge actions: reload from the database, export parts to a MAJ job, open the parts in Nexus.
/// Shown modally from an external command, so the buttons may call the Revit API directly.
/// </summary>
internal sealed class FabricationWindow : Window
{
    private sealed class Row
    {
        public string Name { get; init; } = "";
        public string Group { get; init; } = "";
        public string Abbreviation { get; init; } = "";
        public int Id { get; init; }
        public string Details { get; init; } = "";
    }

    private readonly UIDocument _uidoc;
    private readonly AgentLog _log;
    private readonly ListBox _kinds = new() { MinWidth = 200, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(4, 3, 4, 3) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended,
    };
    private readonly ItemsControl _summary = new();
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private FabricationDatabase? _db;

    public FabricationWindow(UIDocument uidoc, AgentLog log)
    {
        _uidoc = uidoc;
        _log = log;
        Title = "Nexus - Fabrication database";
        Width = 1100;
        Height = 700;
        MinWidth = 700;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        try { new WindowInteropHelper(this).Owner = uidoc.Application.MainWindowHandle; } catch { /* centre on screen */ }

        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding(nameof(Row.Group)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Abbreviation", Binding = new Binding(nameof(Row.Abbreviation)), Width = DataGridLength.Auto });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding(nameof(Row.Id)), Width = DataGridLength.Auto });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new Binding(nameof(Row.Details)), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });

        _summary.ItemTemplate = SummaryTemplate();
        _kinds.SelectionChanged += (_, _) => ShowRows();
        _search.TextChanged += (_, _) => ShowRows();
        _search.ToolTip = "Filter by name, group, abbreviation or details";

        var root = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_title);
        header.Children.Add(_summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("Reload from database", "Pull changes made in Fabrication CADmep (services, item files, custom data) into this model.", OnReload));
        buttons.Children.Add(Button("Export to MAJ…", "Save the selected fabrication parts (or every part in the active view) as a MAJ job for CADmep, ESTmep or CAMduct.", OnExport));
        buttons.Children.Add(Button("Fabrication parts in Nexus", "Open the Fabrication parts view in the Nexus hub to compare and bulk-edit item numbers, spools, statuses and custom data.", OnHub));
        var close = Button("Close", null, (_, _) => Close());
        close.IsCancel = true;
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        DockPanel.SetDock(_kinds, Dock.Left);
        root.Children.Add(_kinds);

        var right = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        right.Children.Add(_search);
        right.Children.Add(_grid);
        root.Children.Add(right);

        Content = root;
        Load();
    }

    private void Load()
    {
        string? kept = (_kinds.SelectedItem as ListBoxItem)?.Tag as string;
        _db = FabricationDatabase.Read(_uidoc.Document);
        _kinds.Items.Clear();
        if (_db is null)
        {
            _title.Text = "No fabrication configuration";
            _summary.ItemsSource = new[] { new KeyValuePair<string, string>("", FabricationDatabase.NotConfigured) };
            _grid.ItemsSource = null;
            return;
        }

        _title.Text = _db.Name.Length > 0 ? _db.Name : "Fabrication configuration";
        _summary.ItemsSource = _db.Summary().Where(p => p.Key != "Configuration" && p.Value.Length > 0).ToList();
        foreach (var w in _db.Warnings) _log.Warn(w);

        foreach (var kind in FabricationDatabase.Kinds.All)
        {
            var item = new ListBoxItem { Content = $"{kind}  ({_db.Count(kind)})", Tag = kind, Padding = new Thickness(6, 4, 6, 4) };
            _kinds.Items.Add(item);
            if (kind == (kept ?? FabricationDatabase.Kinds.Service)) item.IsSelected = true;
        }
        ShowRows();
    }

    private void ShowRows()
    {
        if (_db is null || (_kinds.SelectedItem as ListBoxItem)?.Tag is not string kind)
        {
            _grid.ItemsSource = null;
            return;
        }
        string filter = _search.Text.Trim();
        _grid.ItemsSource = _db.Entries.Where(e => e.Kind == kind)
            .Select(e => new Row
            {
                Name = e.Name,
                Group = e.Group,
                Abbreviation = e.Abbreviation,
                Id = e.Id,
                Details = string.Join("   ·   ", e.Details.Where(d => d.Value.Length > 0).Select(d => $"{d.Key}: {d.Value}")),
            })
            .Where(r => filter.Length == 0 || $"{r.Name} {r.Group} {r.Abbreviation} {r.Details}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        try
        {
            string summary = FabricationActions.Reload(_uidoc.Document);
            _log.Info("Fabrication configuration reloaded. " + summary.Replace('\n', ' '));
            Load();
            MessageBox.Show(this, summary, "Fabrication configuration reloaded", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Warn("Reloading the fabrication configuration failed.", ex);
            MessageBox.Show(this, ex.Message, "Reload failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e) => FabricationCommands.Export(_uidoc, _log, this);

    private void OnHub(object sender, RoutedEventArgs e)
    {
        var error = HubControl.Show(_log, "revit.fabrication.parts");
        if (error is not null) MessageBox.Show(this, error, "Nexus", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static Button Button(string text, string? tip, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 5, 14, 5), ToolTip = tip };
        b.Click += click;
        return b;
    }

    private static DataTemplate SummaryTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var key = new FrameworkElementFactory(typeof(TextBlock));
        key.SetBinding(TextBlock.TextProperty, new Binding("Key"));
        key.SetValue(TextBlock.WidthProperty, 190.0);
        key.SetValue(TextBlock.OpacityProperty, 0.7);
        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetBinding(TextBlock.TextProperty, new Binding("Value"));
        value.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(key);
        panel.AppendChild(value);
        return new DataTemplate { VisualTree = panel };
    }
}
