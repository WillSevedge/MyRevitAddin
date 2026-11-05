using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _vm;
    private DateTime _lastRefresh = DateTime.UtcNow;
    private bool _editingCell;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
        _vm.TableReady += OnTableReady;
        _vm.ColumnsChanged += BuildColumns;
        _vm.CommitGridEdits += () => TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _vm.SelectedRowsProvider = SelectedRows;
        _vm.FocusCellRequested += FocusCell;
        // The health marks show while the Sheet Health pane is open (the Health button's count says when there is something).
        _vm.Health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HealthViewModel.IsOpen)) BuildColumns(_vm.CurrentColumnSpecs());
        };
        // The health mark column is not data: leave it out of copied rows.
        TableGrid.CopyingRowClipboardContent += (_, e) => e.ClipboardRowContent.RemoveAll(c => c.Column is DataGridTemplateColumn);

        // Commit the whole row after each cell edit, so the edit counts as pending straight away.
        TableGrid.CellEditEnding += (_, e) =>
        {
            _editingCell = false;
            if (e.EditAction == DataGridEditAction.Commit)
                Dispatcher.BeginInvoke(() => TableGrid.CommitEdit(DataGridEditingUnit.Row, true));
        };

        // Coming back to the hub: pick up files opened or closed meanwhile.
        Activated += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefresh < AutoRefreshInterval) return;
            _lastRefresh = DateTime.UtcNow;
            await _vm.RefreshIfHostsChangedAsync(force: true);
        };

        InputBindings.Add(new KeyBinding(new RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); return Task.CompletedTask; }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => { OpenPalette(); return Task.CompletedTask; }), Key.K, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => { _vm.IsHome = true; return Task.CompletedTask; }), Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => { _vm.IsData = true; return Task.CompletedTask; }), Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => { _vm.IsConnections = true; return Task.CompletedTask; }), Key.D3, ModifierKeys.Control));

        // The explorer (sources and views) takes no room until a program is connected.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasPrograms)) UpdateExplorer();
        };
        UpdateExplorer();
    }

    // ------------------------------------------------------------------ shell

    public static readonly DependencyProperty CompactCommandsProperty =
        DependencyProperty.Register(nameof(CompactCommands), typeof(bool), typeof(MainWindow));
    public static readonly DependencyProperty HideSecondaryProperty =
        DependencyProperty.Register(nameof(HideSecondary), typeof(bool), typeof(MainWindow));

    /// <summary>The command bar is narrow: icons only (labels stay in the tooltips).</summary>
    public bool CompactCommands
    {
        get => (bool)GetValue(CompactCommandsProperty);
        set => SetValue(CompactCommandsProperty, value);
    }

    /// <summary>The command bar is very narrow: the tools move to the overflow menu (⋯).</summary>
    public bool HideSecondary
    {
        get => (bool)GetValue(HideSecondaryProperty);
        set => SetValue(HideSecondaryProperty, value);
    }

    private void OnCommandBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The toolbar row needs ~1100 px with the Show in model label and ~940 px with every tool shown.
        double width = e.NewSize.Width;
        CompactCommands = width < 1100;
        HideSecondary = width < 940;
    }

    private GridLength _explorerWidth = new(256);

    private void UpdateExplorer()
    {
        if (_vm.HasPrograms)
        {
            ExplorerColumn.MinWidth = 200;
            ExplorerColumn.Width = _explorerWidth;
        }
        else
        {
            if (ExplorerColumn.Width.Value > 0) _explorerWidth = ExplorerColumn.Width;
            ExplorerColumn.MinWidth = 0;
            ExplorerColumn.Width = new GridLength(0);
        }
    }

    private void OnToggleRail(object sender, RoutedEventArgs e) => _vm.IsRailExpanded = !_vm.IsRailExpanded;

    private void OnSettingsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindResource("SettingsMenu") is not ContextMenu menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    private void OnShowConnections(object sender, RoutedEventArgs e) => _vm.IsConnections = true;

    private void OnGoData(object sender, RoutedEventArgs e) => _vm.IsData = true;

    private void OnOpenHealth(object sender, RoutedEventArgs e) => _vm.Health.IsOpen = true;

    /// <summary>Closing the inspector also unpins it (it stays closed next time).</summary>
    private void OnCloseInspector(object sender, RoutedEventArgs e)
    {
        _vm.IsInspectorPinned = false;
        _vm.ShowDetails = false;
    }

    // ------------------------------------------------------------------ columns

    private void OnTableReady(DataTable table, IReadOnlyList<GridColumnSpec> specs)
    {
        TableGrid.ItemsSource = null;
        BuildColumns(specs);
        TableGrid.ItemsSource = table.DefaultView;
    }

    private void BuildColumns(IReadOnlyList<GridColumnSpec> specs)
    {
        TableGrid.Columns.Clear();
        var baseCellStyle = TableGrid.TryFindResource(typeof(DataGridCell)) as Style;
        var editor = TableGrid.TryFindResource("CellEditor") as Style;
        bool health = _vm.Health.IsAvailable && _vm.Health.IsOpen;
        if (health) TableGrid.Columns.Add(HealthColumn(baseCellStyle));
        // Headers are one line; the group ("Title Block (Type)") is added only where two shown columns share a name.
        var shared = specs.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            var column = new DataGridTextColumn
            {
                Header = Header(spec, shared.Contains(spec.Name)),
                Binding = new Binding(spec.Column) { Mode = spec.Editable ? BindingMode.TwoWay : BindingMode.OneWay },
                SortMemberPath = spec.Column,
                MaxWidth = 360,
                MinWidth = 56,
                IsReadOnly = !spec.Editable,
                EditingElementStyle = editor,
            };
            if (spec.Editable) column.CellStyle = CellStyle(spec.Column, baseCellStyle);
            else
            {
                var style = new Style(typeof(DataGridCell), baseCellStyle);
                style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Nexus.TextSecondary")));
                column.CellStyle = style;
            }
            TableGrid.Columns.Add(column);
        }
        TableGrid.FrozenColumnCount = specs.Count(s => s.Frozen) + (health ? 1 : 0);
    }

    // ------------------------------------------------------------------ sheet health

    /// <summary>Narrow first column: a check mark, or the worst health finding for the sheet (hover for the list).</summary>
    private DataGridTemplateColumn HealthColumn(Style? baseCellStyle)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontFamilyProperty, FindResource("IconFont"));
        text.SetValue(TextBlock.FontSizeProperty, 14.0);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, HealthBinding(HealthAspect.Glyph));
        text.SetBinding(TextBlock.ForegroundProperty, HealthBinding(HealthAspect.Brush));
        text.SetBinding(ToolTipProperty, HealthBinding(HealthAspect.ToolTip));

        var header = new TextBlock { Text = "\uE95E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, ToolTip = "Sheet health" };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Nexus.TextTertiary");

        var cellStyle = new Style(typeof(DataGridCell), baseCellStyle);
        cellStyle.Setters.Add(new EventSetter(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _vm.Health.IsOpen = true)));
        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new DataTemplate { VisualTree = text },
            Width = 34,
            CanUserSort = false,
            CanUserResize = false,
            CanUserReorder = false,
            IsReadOnly = true,
            CellStyle = cellStyle,
        };
    }

    private MultiBinding HealthBinding(HealthAspect aspect)
    {
        var binding = new MultiBinding { Converter = new HealthMarkConverter(_vm.Health, aspect) };
        binding.Bindings.Add(new Binding());                                       // the DataRowView
        binding.Bindings.Add(new Binding("Health.Version") { Source = _vm });      // re-evaluate after every check
        return binding;
    }

    private void OnCloseHealth(object sender, RoutedEventArgs e) => _vm.Health.IsOpen = false;

    // ------------------------------------------------------------------ rename and renumber

    private void OnRename(object sender, RoutedEventArgs e) => Fire(OpenRenameAsync);

    private void OnPrint(object sender, RoutedEventArgs e) => Fire(OpenPrintAsync);

    /// <summary>Starts work from a click or key; an error reaches the app's handler (the info bar) instead of being lost.</summary>
    private static async void Fire(Func<Task> work) => await work();

    /// <summary>Print &amp; PDF works on sheets: from another view it switches to Sheets first, then opens.</summary>
    private async Task OpenPrintAsync()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!_vm.IsSheetsView || !_vm.IsData)
        {
            if (!await _vm.ShowSheetsAsync()) return;
        }
        var selected = SelectedRows().ToHashSet();
        var shown = TableGrid.Items.OfType<DataRowView>().ToList();
        bool selection = selected.Count > 1;
        var views = selection ? shown.Where(selected.Contains).ToList() : shown;
        var print = _vm.CreatePrint(views, selection);
        if (print is null)
        {
            _vm.IsData = true;
            _vm.Notify(NoticeKind.Info, "No sheets loaded yet",
                _vm.HasPrograms
                    ? "Tick the files to print under Sources on the left (or Reload), then open Print & PDF again."
                    : "Open Revit or AutoCAD with the Nexus add-in; once its files show here and are ticked, open Print & PDF again.");
            return;
        }
        new PrintWindow(print) { Owner = this }.Show();
    }

    private void OnHistory(object sender, RoutedEventArgs e) => OpenHistory();

    private MarkupsWindow? _markups;

    private void OnMarkups(object sender, RoutedEventArgs e) => OpenMarkups();

    /// <summary>Markups from Bluebeam: one window, brought forward when already open.</summary>
    private void OpenMarkups()
    {
        if (_markups is not null)
        {
            _markups.Activate();
            return;
        }
        _markups = new MarkupsWindow(new MarkupsViewModel(_vm)) { Owner = this };
        _markups.Closed += (_, _) => _markups = null;
        _markups.Show();
    }

    /// <summary>⋯ › Connect Claude: adds Nexus's MCP server to Claude Desktop and copies the Claude Code command.</summary>
    private void OnConnectClaude(object sender, RoutedEventArgs e)
    {
        string? server = McpSetup.ServerPath();
        if (server is null)
        {
            Dialogs.Message("The MCP server is not installed",
                "nexus-mcp.exe is built and installed with the hub: double-click Install-Hub.cmd in the Nexus folder " +
                "(or run build\\Publish-Hub.cmd install), then try again.", NoticeKind.Info, this);
            return;
        }
        string claudeCode = McpSetup.ClaudeCodeCommand(server);
        try { Clipboard.SetText(claudeCode); } catch { /* clipboard busy */ }
        try
        {
            var changed = McpSetup.ConnectClaudeDesktop(server);
            Dialogs.Message("Claude can now use AutoCAD through Nexus",
                (changed.Count > 0
                    ? $"Added \"nexus\" to Claude Desktop ({string.Join(", ", changed)}); your other MCP servers are unchanged and a backup was saved. Quit and restart Claude Desktop.\n\n"
                    : "Claude Desktop was not found on this PC.\n\n") +
                $"For Claude Code, this command is on the clipboard:\n{claudeCode}\n\n" +
                "Then open AutoCAD with a drawing and ask, for example: \"What layers are in this drawing?\" or \"Draw a 20' x 30' rectangle on A-WALL\".",
                NoticeKind.Success, this);
        }
        catch (Exception ex)
        {
            Dialogs.Message("Claude Desktop was not changed", ex.Message + $"\n\nAdd this server by hand: {server}", NoticeKind.Warning, this);
        }
    }

    /// <summary>⋯ › Theme: tick the current choice when the submenu opens.</summary>
    private void OnThemeMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = Equals(item.Tag, _vm.Theme);
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string theme }) _vm.Theme = theme;
    }

    private void OnPaletteMenu(object sender, RoutedEventArgs e) => OpenPalette();

    // ------------------------------------------------------------------ command palette (Ctrl+K)

    private void OpenPalette()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        new PaletteWindow(new PaletteViewModel(PaletteItems()), this).Show();
    }

    /// <summary>Everything the palette can do right now: actions, views, and every sheet (or item) loaded.</summary>
    private List<PaletteItem> PaletteItems()
    {
        var items = new List<PaletteItem>();
        void Action(string title, string glyph, Action run, string subtitle = "", string keywords = "", bool when = true)
        {
            if (when) items.Add(new PaletteItem { Title = title, Subtitle = subtitle, Glyph = glyph, Category = "Action", Run = run, Keywords = keywords });
        }
        void Command(string title, string glyph, ICommand command, string subtitle = "", string keywords = "") =>
            Action(title, glyph, () => { if (command.CanExecute(null)) command.Execute(null); }, subtitle, keywords, command.CanExecute(null));

        Command("Review & apply changes", "\uE73E", _vm.ApplyChangesCommand, _vm.PendingText, "save write commit apply");
        Action("Print & PDF", "\uE749", () => Fire(OpenPrintAsync), "PDFs, preview and printing of the sheets shown", "plot print pdf publish preview batch", _vm.HasPrograms);
        Action("Markups (Bluebeam)", "\uE70F", OpenMarkups, "Comments made in Bluebeam on the PDFs, by sheet", "bluebeam revu markups comments review pdf annotations", true);
        Command("Discard changes", "\uE7A7", _vm.DiscardChangesCommand, "Undo every edit not applied yet", "undo revert cancel");
        Action("Rename & Renumber…", "\uE8AC", () => Fire(OpenRenameAsync), "Sheet numbers and names of the selected sheets, or every sheet shown", "renumber sequence replace find case prefix suffix sheet number name");
        Action("Sheet health", "\uE95E", () => _vm.Health.IsOpen = true, _vm.Health.Summary, "check qa qc issues errors duplicates", _vm.Health.IsAvailable);
        Action("History and snapshots", "\uE81C", OpenHistory, "What changed since an issue; change log", "snapshot compare changes log audit");
        Command("Show in model", "\uE8A7", _vm.ShowInModelCommand, "Open or zoom to the selected rows in their program", "zoom select open");
        Command("Reload", "\uE72C", _vm.RefreshCommand, "Read the data again (F5)", "refresh read");
        Action("Choose columns…", "\uE71D", () => OnColumns(this, new RoutedEventArgs()), "", "fields parameters show hide");
        Command("Export to Model", "\uE9F9", _vm.UpdateExcelCommand, "", "excel update push fill budget model export");
        Command("Compare with the deliverables list", "\uE9F9", _vm.CompareExcelCommand, "", "excel compare deliverables budget");
        Command("Choose workbook…", "\uE9F9", _vm.LinkExcelCommand, "", "excel link workbook");
        Command("Clear workbook", "\uE9F9", _vm.ClearWorkbookCommand, "", "excel clear forget remove workbook");
        Command("Export this view to Excel…", "\uE9F9", _vm.ExportExcelCommand, "", "excel export xlsx");
        Action("Home", "\uE80F", () => _vm.IsHome = true, "Ctrl+1", "workspace start dashboard");
        Action("Sheets & data", "\uE80A", () => _vm.IsData = true, "Ctrl+2", "workspace table grid");
        Action("Connections", "\uE703", () => _vm.IsConnections = true, _vm.ConnectionSummary + "  ·  Ctrl+3", "workspace programs revit autocad excel claude status");
        Action(_vm.ShowDetails ? "Hide the inspector" : "Show the inspector", "\uE8A0", () => _vm.ShowDetails = !_vm.ShowDetails, "Every property of the selected row", "details properties pane panel");
        Action(_vm.IsCompact ? "Comfortable rows" : "Compact rows", "\uE8FD", () => _vm.IsCompact = !_vm.IsCompact, "Table density", "density row height dense");
        Action(_vm.IsRailExpanded ? "Collapse the navigation" : "Expand the navigation", "\uE700", () => _vm.IsRailExpanded = !_vm.IsRailExpanded, "", "rail sidebar menu");
        Action("Connect Claude (AI assistants)…", "\uE8F2", () => OnConnectClaude(this, new RoutedEventArgs()), "", "mcp ai assistant claude");
        Command("Sheet index (dummy sheets for CAD drawings)", "\uE8A5", _vm.SheetIndexCommand, "Keep Revit dummy sheets matched to their DWGs", "index dummy placeholder sheet list revision cad dwg");
        Action("Search the table", "\uE721", () => { SearchBox.Focus(); SearchBox.SelectAll(); }, "Ctrl+F", "filter find");

        foreach (var d in _vm.Datasets)
            items.Add(new PaletteItem
            {
                Title = d.Title, Subtitle = d.Category, Glyph = d.Icon, Category = "View",
                Run = () => _vm.SelectedDataset = d, Keywords = "view " + d.Description,
            });

        foreach (var row in _vm.LoadedRows)
        {
            if (row.Item.Trim().Length == 0) continue;
            var target = row;
            items.Add(new PaletteItem
            {
                Title = row.Item.Trim(),
                Subtitle = $"{row.Document}  ·  {row.Host}",
                Glyph = "\uE7C3",
                Category = "Sheet",
                Keywords = row.Key,
                Run = () => _vm.FocusCell(target, null),
                RunAlternate = () =>
                {
                    _vm.FocusCell(target, null);
                    if (_vm.ShowInModelCommand.CanExecute(null)) _vm.ShowInModelCommand.Execute(null);
                },
            });
        }
        return items;
    }

    private void OpenHistory()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        new HistoryWindow(new HistoryViewModel(_vm)) { Owner = this }.ShowDialog();
    }

    /// <summary>The selected rows (more than one), else every row shown, in the order shown.</summary>
    /// <summary>Rename &amp; Renumber: from a view without sheet numbers and names, it switches to Sheets first.</summary>
    private async Task OpenRenameAsync()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!_vm.IsSheetsView && _vm.RenameFields(TableGrid.Items.OfType<DataRowView>().Select(_vm.TableRowOf).OfType<Nexus.Hub.Core.TableRow>().ToList()).Count == 0)
        {
            if (!await _vm.ShowSheetsAsync()) return;
        }
        var selected = SelectedRows().ToHashSet();
        var shown = TableGrid.Items.OfType<DataRowView>().ToList();
        var views = selected.Count > 1 ? shown.Where(selected.Contains).ToList() : shown;
        var rows = views.Select(_vm.TableRowOf).OfType<Nexus.Hub.Core.TableRow>().ToList();
        if (rows.Count == 0)
        {
            _vm.Status = "Nothing to rename: load some data first.";
            return;
        }
        var fields = _vm.RenameFields(rows);
        if (fields.Count == 0)
        {
            _vm.Status = "Rename & Renumber changes sheet numbers and names: open the Sheets view (or select sheets that can be edited).";
            return;
        }
        string scope = selected.Count > 1 ? $"{rows.Count} selected rows, in the order shown" : $"All {rows.Count} rows shown, in the order shown";
        new RenameWindow(new RenameViewModel(_vm, rows, scope, fields)) { Owner = this }.ShowDialog();
    }

    /// <summary>Selects one cell (from the health list) and scrolls it into view.</summary>
    private void FocusCell(int rowIndex, string gridColumn)
    {
        var view = TableGrid.Items.OfType<DataRowView>().FirstOrDefault(v => MainViewModel.RowIndex(v) == rowIndex);
        if (view is null)
        {
            _vm.Status = "That sheet is hidden by the search. Clear the search to see it.";
            return;
        }
        var column = TableGrid.Columns.FirstOrDefault(c => c.SortMemberPath == gridColumn)
                     ?? TableGrid.Columns.FirstOrDefault(c => c.SortMemberPath is { Length: > 0 });
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TableGrid.SelectedCells.Clear();
        TableGrid.ScrollIntoView(view, column);
        if (column is not null)
        {
            var cell = new DataGridCellInfo(view, column);
            TableGrid.SelectedCells.Add(cell);
            TableGrid.CurrentCell = cell;
        }
        TableGrid.Focus();
    }

    /// <summary>
    /// One-line header: the name; the group in a lighter tone when another shown column has the same name; a small
    /// lock on columns that cannot be edited. The full "group › name" is in the tooltip.
    /// </summary>
    private object Header(GridColumnSpec spec, bool qualify)
    {
        bool mapped = spec.Group.Length == 0 || spec.Group == Nexus.Hub.Core.SheetFieldMap.Group;
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(spec.Name));
        if (qualify && !mapped)
        {
            // "Title Block (Type) · Identity Data" → "Title Block (Type)"
            string where = spec.Group.Split(new[] { " · " }, 2, StringSplitOptions.None)[0];
            var run = new System.Windows.Documents.Run("  " + where) { FontWeight = FontWeights.Normal };
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Nexus.TextTertiary");
            text.Inlines.Add(run);
        }
        var panel = new DockPanel
        {
            ToolTip = (mapped ? spec.Name : $"{spec.Group} › {spec.Name}") + (spec.Editable ? "" : "\nRead-only"),
        };
        if (!spec.Editable)
        {
            var lockGlyph = new TextBlock
            {
                Text = "\uE72E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10,
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            lockGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Nexus.TextTertiary");
            DockPanel.SetDock(lockGlyph, Dock.Right);
            panel.Children.Add(lockGlyph);
        }
        panel.Children.Add(text);
        return panel;
    }

    /// <summary>Edited cells are highlighted; cells that cannot be edited are greyed, with the reason as a tooltip.</summary>
    private Style CellStyle(string column, Style? basedOn)
    {
        var style = new Style(typeof(DataGridCell), basedOn);
        style.Setters.Add(new Setter(BackgroundProperty, StateBinding(column, CellAspect.Background)));
        style.Setters.Add(new Setter(ForegroundProperty, StateBinding(column, CellAspect.Foreground)));
        style.Setters.Add(new Setter(ToolTipProperty, StateBinding(column, CellAspect.ToolTip)));
        return style;
    }

    private MultiBinding StateBinding(string column, CellAspect aspect)
    {
        var binding = new MultiBinding { Converter = new CellStateConverter(_vm, column, aspect) };
        binding.Bindings.Add(new Binding(column)); // re-evaluates when the value changes
        binding.Bindings.Add(new Binding());       // the DataRowView
        return binding;
    }

    private void OnColumns(object sender, RoutedEventArgs e)
    {
        var groups = _vm.BuildColumnGroups();
        if (groups.Count == 0)
        {
            _vm.Status = "Load some data first.";
            return;
        }
        var window = new ColumnsWindow(groups) { Owner = this };
        if (window.ShowDialog() == true) _vm.SetVisibleColumns(window.CheckedColumnIds);
    }

    private void OnDropDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    /// <summary>The views list sits inside the sidebar's scroll viewer: pass the mouse wheel on to it.</summary>
    private void OnNestedScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement element) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(element) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private void OnMessages(object sender, RoutedEventArgs e) => Dialogs.ShowMessages(this, _vm.IssueLines(), _vm.Log, _vm.EditingReport());

    // ------------------------------------------------------------------ selection

    private void OnSelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var current = TableGrid.CurrentCell.Item as DataRowView
                      ?? TableGrid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().FirstOrDefault();
        if (current is not null && !ReferenceEquals(current, _vm.SelectedRow)) _vm.SelectedRow = current;
        _vm.SelectionCount = SelectedRows().Count;
    }

    private IReadOnlyList<DataRowView> SelectedRows() =>
        TableGrid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Distinct().ToList();

    /// <summary>Selected cells in display order (rows as sorted/filtered, columns as arranged).</summary>
    private List<(DataRowView View, string Column, int RowIndex, int ColumnIndex)> SelectedCellsOrdered()
    {
        var rowIndex = new Dictionary<object, int>();
        var list = new List<(DataRowView, string, int, int)>();
        foreach (var cell in TableGrid.SelectedCells)
        {
            if (cell.Item is not DataRowView view || cell.Column is not DataGridBoundColumn { Binding: Binding b }) continue;
            if (!rowIndex.TryGetValue(view, out int r)) rowIndex[view] = r = TableGrid.Items.IndexOf(view);
            list.Add((view, b.Path.Path, r, cell.Column.DisplayIndex));
        }
        return list.OrderBy(c => c.Item3).ThenBy(c => c.Item4).ToList();
    }

    private List<DataGridBoundColumn> ColumnsInDisplayOrder() =>
        TableGrid.Columns.OfType<DataGridBoundColumn>().OrderBy(c => c.DisplayIndex).ToList();

    // ------------------------------------------------------------------ editing

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not DataRowView view || e.Column is not DataGridBoundColumn { Binding: Binding b })
        {
            e.Cancel = true;
            return;
        }
        var blocker = _vm.EditBlocker(view, b.Path.Path);
        if (blocker is null)
        {
            _editingCell = true;
            return;
        }
        e.Cancel = true;
        _vm.Status = "Cannot edit: " + blocker;
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingCell) return; // the cell's text box handles its own keys
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.V when ctrl: Paste(); e.Handled = true; break;
            case Key.D when ctrl: FillDown(); e.Handled = true; break;
            case Key.H when ctrl: FindReplace(); e.Handled = true; break;
            case Key.R when ctrl: Fire(OpenRenameAsync); e.Handled = true; break;
            case Key.Z when ctrl: Revert(); e.Handled = true; break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None: Clear(); e.Handled = true; break;
            case Key.F2 when SelectedCellsOrdered().Count > 1: SetValue(); e.Handled = true; break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None && ToggleYesNo(): e.Handled = true; break;
        }
    }

    /// <summary>
    /// Right-click on a row that is not part of the selection selects just that row (so the menu acts on it);
    /// right-click inside a selection keeps it (to act on all selected sheets).
    /// </summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not DataRowView view) return;
        if (SelectedRows().Contains(view)) return;
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TableGrid.SelectedCells.Clear();
        foreach (var column in TableGrid.Columns)
            TableGrid.SelectedCells.Add(new DataGridCellInfo(view, column));
        TableGrid.CurrentCell = new DataGridCellInfo(view, cell.Column);
        cell.Focus();
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private readonly List<(MenuItem Item, string ColumnId, string Label)> _revisionItems = new();

    /// <summary>Builds the Revisions submenu for the selected sheets each time the menu opens.</summary>
    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var items = TableGrid.ContextMenu?.Items.OfType<FrameworkElement>().ToList() ?? new();
        if (items.FirstOrDefault(i => i.Name == "RevisionsMenu") is not MenuItem RevisionsMenu) return;
        var RevisionsSeparator = items.FirstOrDefault(i => i.Name == "RevisionsSeparator") ?? new Separator();
        RevisionsMenu.Items.Clear();
        _revisionItems.Clear();

        var rows = SelectedRows();
        var states = _vm.RevisionStates(rows);
        bool show = states.Count > 0;
        RevisionsMenu.Visibility = RevisionsSeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        int sheets = _vm.RevisionSheetCount(rows);
        RevisionsMenu.Header = sheets == 1 ? "Revisions on this sheet" : $"Revisions on these {sheets} sheets";
        foreach (var (columnId, label, _, _) in states)
        {
            var item = new MenuItem { StaysOpenOnClick = true };
            item.Click += (_, _) => ToggleRevision(columnId);
            _revisionItems.Add((item, columnId, label));
            RevisionsMenu.Items.Add(item);
        }
        RevisionsMenu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all revisions", StaysOpenOnClick = true };
        all.Click += (_, _) => SetAllRevisions(true);
        var none = new MenuItem { Header = "Remove all revisions", StaysOpenOnClick = true };
        none.Click += (_, _) => SetAllRevisions(false);
        RevisionsMenu.Items.Add(all);
        RevisionsMenu.Items.Add(none);
        RefreshRevisionItems();
    }

    /// <summary>Tick = on every selected sheet; dash = on some of them; locked ones say why.</summary>
    private void RefreshRevisionItems()
    {
        var states = _vm.RevisionStates(SelectedRows()).ToDictionary(s => s.ColumnId);
        foreach (var (item, columnId, label) in _revisionItems)
        {
            if (!states.TryGetValue(columnId, out var s)) continue;
            item.IsChecked = s.State == true;
            item.Icon = s.State is null ? new TextBlock { Text = "–", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center } : null;
            item.Header = s.Locked > 0 ? $"{label}   (by revision clouds on {s.Locked})" : label;
            item.ToolTip = s.State is null ? "On some of the selected sheets. Click to add it to all of them." : null;
        }
    }

    /// <summary>Click a revision: add it to every selected sheet, or remove it if all of them already show it.</summary>
    private void ToggleRevision(string columnId)
    {
        var rows = SelectedRows();
        var state = _vm.RevisionStates(rows).FirstOrDefault(s => s.ColumnId == columnId);
        if (state.ColumnId is null) return;
        _vm.SetRevisions(rows, new Dictionary<string, bool> { [columnId] = state.State != true });
        RefreshRevisionItems();
    }

    private void SetAllRevisions(bool show)
    {
        var rows = SelectedRows();
        var changes = _vm.RevisionStates(rows).ToDictionary(s => s.ColumnId, _ => show);
        _vm.SetRevisions(rows, changes);
        RefreshRevisionItems();
    }

    /// <summary>Space on selected Yes/No cells: all become Yes, or all No if they already are.</summary>
    private bool ToggleYesNo()
    {
        var cells = SelectedCellsOrdered().Where(c => _vm.IsPropertyColumn(c.Column)).ToList();
        if (cells.Count == 0) return false;
        var values = cells.Select(c => (c.View[c.Column] as string ?? "").Trim()).ToList();
        if (!values.All(v => v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v.Equals("No", StringComparison.OrdinalIgnoreCase)))
            return false;
        string next = values.All(v => v.Equals("Yes", StringComparison.OrdinalIgnoreCase)) ? "No" : "Yes";
        int done = 0;
        var skipped = new List<string>();
        foreach (var c in cells)
        {
            var reason = _vm.TrySetCell(c.View, c.Column, next);
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report($"Set to {next}:", done, skipped);
        return true;
    }

    private void OnPaste(object sender, RoutedEventArgs e) => Paste();
    private void OnFillDown(object sender, RoutedEventArgs e) => FillDown();
    private void OnFindReplace(object sender, RoutedEventArgs e) => FindReplace();
    private void OnSetValue(object sender, RoutedEventArgs e) => SetValue();
    private void OnRevert(object sender, RoutedEventArgs e) => Revert();
    private void OnClear(object sender, RoutedEventArgs e) => Clear();

    /// <summary>
    /// Pastes text copied from Excel (tab/newline separated). One value fills every selected cell;
    /// a block is pasted from the top-left selected cell across and down.
    /// </summary>
    private void Paste()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Clipboard.ContainsText()) return;
        var lines = Clipboard.GetText().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var block = lines.Select(l => l.Split('\t')).ToList();
        var selected = SelectedCellsOrdered();
        if (selected.Count == 0)
        {
            _vm.Status = "Select the cell to paste into first.";
            return;
        }

        int done = 0;
        var skipped = new List<string>();
        void Set(DataRowView view, string column, string value)
        {
            var reason = _vm.TrySetCell(view, column, value);
            if (reason is null) done++;
            else skipped.Add(reason);
        }

        if (block.Count == 1 && block[0].Length == 1)
        {
            foreach (var c in selected) Set(c.View, c.Column, block[0][0]);
        }
        else
        {
            var columns = ColumnsInDisplayOrder();
            var anchor = selected[0];
            int startColumn = columns.FindIndex(c => ((Binding)c.Binding).Path.Path == anchor.Column);
            for (int i = 0; i < block.Count; i++)
            {
                int r = anchor.RowIndex + i;
                if (r >= TableGrid.Items.Count || TableGrid.Items[r] is not DataRowView view) break;
                for (int j = 0; j < block[i].Length; j++)
                {
                    int c = startColumn + j;
                    if (c >= columns.Count) break;
                    Set(view, ((Binding)columns[c].Binding).Path.Path, block[i][j]);
                }
            }
        }
        Report("Pasted", done, skipped);
    }

    /// <summary>Copies the first selected value in each column down to the other selected cells of that column.</summary>
    private void FillDown()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        int done = 0;
        var skipped = new List<string>();
        foreach (var column in SelectedCellsOrdered().GroupBy(c => c.Column))
        {
            var cells = column.OrderBy(c => c.RowIndex).ToList();
            if (cells.Count < 2) continue;
            string value = cells[0].View[column.Key] as string ?? "";
            foreach (var c in cells.Skip(1))
            {
                var reason = _vm.TrySetCell(c.View, c.Column, value);
                if (reason is null) done++;
                else skipped.Add(reason);
            }
        }
        Report("Filled", done, skipped);
    }

    private void SetValue()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var cells = SelectedCellsOrdered();
        if (cells.Count == 0) return;
        var value = Dialogs.AskValue(this, "Set value", $"New value for the {cells.Count} selected cell(s):",
            cells[0].View[cells[0].Column] as string ?? "");
        if (value is null) return;
        int done = 0;
        var skipped = new List<string>();
        foreach (var c in cells)
        {
            var reason = _vm.TrySetCell(c.View, c.Column, value);
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Set", done, skipped);
    }

    private void FindReplace()
    {
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = SelectedCellsOrdered();
        var request = Dialogs.AskFindReplace(this, selected.Count > 1);
        if (request is null) return;

        IEnumerable<(DataRowView View, string Column)> cells = request.SelectionOnly
            ? selected.Select(c => (c.View, c.Column))
            : TableGrid.Items.OfType<DataRowView>().SelectMany(v => ColumnsInDisplayOrder()
                .Select(col => ((Binding)col.Binding).Path.Path)
                .Where(_vm.IsPropertyColumn)
                .Select(col => (v, col)));

        var comparison = request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int done = 0;
        var skipped = new List<string>();
        foreach (var (view, column) in cells.ToList())
        {
            string current = view[column] as string ?? "";
            string? next = request.WholeCell
                ? (string.Equals(current.Trim(), request.Find, comparison) ? request.Replace : null)
                : (current.Contains(request.Find, comparison) ? current.Replace(request.Find, request.Replace, comparison) : null);
            if (next is null) continue;
            var reason = _vm.TrySetCell(view, column, next);
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Replaced", done, skipped);
    }

    private void Clear()
    {
        var cells = SelectedCellsOrdered().Where(c => _vm.IsPropertyColumn(c.Column)).ToList();
        int done = 0;
        var skipped = new List<string>();
        foreach (var c in cells)
        {
            var reason = _vm.TrySetCell(c.View, c.Column, "");
            if (reason is null) done++;
            else skipped.Add(reason);
        }
        Report("Cleared", done, skipped);
    }

    private void Revert() => _vm.Revert(SelectedCellsOrdered().Select(c => (c.View, c.Column)));

    private void Report(string verb, int done, List<string> skipped)
    {
        _vm.Status = skipped.Count == 0
            ? $"{verb} {done} cell(s). Changes are pending until you apply them."
            : $"{verb} {done} cell(s); {skipped.Count} skipped ({skipped.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key}).";
    }

    private void OnDetailKeyDown(object sender, KeyEventArgs e)
    {
        // Enter in a single-line detail value commits it (Shift+Enter keeps a line break).
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}

internal enum CellAspect { Background, Foreground, ToolTip }

internal enum HealthAspect { Glyph, Brush, ToolTip }

/// <summary>The sheet's health mark: ✓, or the worst finding's icon and colour, with the findings as a tooltip.</summary>
internal sealed class HealthMarkConverter : IMultiValueConverter
{
    private readonly HealthViewModel _health;
    private readonly HealthAspect _aspect;

    public HealthMarkConverter(HealthViewModel health, HealthAspect aspect)
    {
        _health = health;
        _aspect = aspect;
    }

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 1 || values[0] is not DataRowView view) return DependencyProperty.UnsetValue;
        int row = MainViewModel.RowIndex(view);
        if (!_health.IsSheet(row)) return _aspect == HealthAspect.Glyph ? "" : DependencyProperty.UnsetValue;
        var worst = _health.Worst(row);
        switch (_aspect)
        {
            case HealthAspect.Glyph:
                return worst switch
                {
                    Nexus.Hub.Core.Health.HealthSeverity.Error => "\uEA39",
                    Nexus.Hub.Core.Health.HealthSeverity.Warning => "\uE7BA",
                    Nexus.Hub.Core.Health.HealthSeverity.Suggestion => "\uE946",
                    _ => "\uE73E",
                };
            case HealthAspect.Brush:
                return Theme(worst switch
                {
                    Nexus.Hub.Core.Health.HealthSeverity.Error => ("SystemFillColorCriticalBrush", Color.FromRgb(0xC4, 0x2B, 0x1C)),
                    Nexus.Hub.Core.Health.HealthSeverity.Warning => ("SystemFillColorCautionBrush", Color.FromRgb(0x9D, 0x5D, 0x00)),
                    Nexus.Hub.Core.Health.HealthSeverity.Suggestion => ("SystemFillColorAttentionBrush", Color.FromRgb(0x00, 0x5F, 0xB8)),
                    _ => ("SystemFillColorSuccessBrush", Color.FromRgb(0x0F, 0x7B, 0x0F)),
                });
            default:
                var issues = _health.IssuesFor(row);
                return issues.Count == 0 ? "No issues found" : string.Join(Environment.NewLine, issues.Select(i => "• " + i.Message));
        }
    }

    private static Brush Theme((string Key, Color Fallback) brush) =>
        Application.Current?.TryFindResource(brush.Key) as Brush ?? new SolidColorBrush(brush.Fallback);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class CellStateConverter : IMultiValueConverter
{
    // Theme brushes (light or dark), with fallbacks if the theme does not define them.
    private static Brush EditedBackground => Theme("SystemFillColorCautionBackgroundBrush", Color.FromRgb(0xFF, 0xF1, 0xB8));
    private static Brush LockedForeground => Theme("TextFillColorTertiaryBrush", Color.FromRgb(0x80, 0x80, 0x80));

    private static Brush Theme(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));

    private readonly MainViewModel _vm;
    private readonly string _column;
    private readonly CellAspect _aspect;

    public CellStateConverter(MainViewModel vm, string column, CellAspect aspect)
    {
        _vm = vm;
        _column = column;
        _aspect = aspect;
    }

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not DataRowView view) return DependencyProperty.UnsetValue;
        bool edited = _vm.IsEdited(view, _column, out var original);
        switch (_aspect)
        {
            case CellAspect.Background:
                return edited ? EditedBackground : DependencyProperty.UnsetValue;
            case CellAspect.Foreground:
                return !edited && _vm.EditBlocker(view, _column) is not null ? LockedForeground : DependencyProperty.UnsetValue;
            default:
                if (edited) return $"Edited. Was: {original}";
                var blocker = _vm.EditBlocker(view, _column);
                return blocker is null ? DependencyProperty.UnsetValue : "Not editable: " + blocker;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
