using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.Hub.Core;
using Nexus.Hub.ViewModels;
using Nexus.Rename;

namespace Nexus.Hub;

/// <summary>
/// Rename &amp; Renumber in the hub, laid out like Bulk Rename in Revit (and Bulk Rename Utility): what to rename on the
/// left, the live preview on the right, every rule panel visible below. Same rules engine and same layout file as
/// the Revit add-in, so presets work in both. The changes become pending edits (Review &amp; apply writes them).
/// </summary>
internal sealed class BulkRenameHubWindow
{
    private const string LastRulesFile = "rename-last.json";

    private readonly MainViewModel _main;
    private readonly IReadOnlyList<TableRow> _shown;
    private readonly IReadOnlyList<TableRow> _selected;
    private readonly Window _window;
    private readonly ObservableCollection<FieldChoice> _fields = new();
    private readonly ObservableCollection<HubPreviewRow> _rows = new();
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _refresh;
    private readonly RenamePresets _presets = new(RenamePresets.DefaultFolder);
    private readonly Dictionary<string, (TableRow Row, string ColumnId)> _sources = new(StringComparer.Ordinal);
    private RenameRules _rules;
    private List<RenameResult> _plan = new();
    private TextBox? _lastText;

    private readonly ComboBox _scope, _preset;
    private readonly DataGrid _grid;
    private readonly FrameworkElement _rulesPanel;
    private readonly TextBlock _summary;
    private readonly TextBox _search;
    private readonly CheckBox _changesOnly;
    private readonly Button _apply, _token;

    public BulkRenameHubWindow(MainViewModel main, IReadOnlyList<TableRow> shown, IReadOnlyList<TableRow> selected, IReadOnlyList<RenameField> fields, Window owner)
    {
        _main = main;
        _shown = shown;
        _selected = selected;
        _rules = LoadLastRules();

        _window = LoadLayout();
        _window.Title = "Nexus - Rename & Renumber";
        _window.Owner = owner;
        try { _window.Icon = owner.Icon; } catch { /* default icon */ }
        ApplyTheme(_window, AppTheme.IsDark);

        T Find<T>(string name) where T : class => _window.FindName(name) as T ?? throw new InvalidOperationException($"Layout part '{name}' is missing.");
        _scope = Find<ComboBox>("ScopeCombo");
        _preset = Find<ComboBox>("PresetCombo");
        _grid = Find<DataGrid>("PreviewGrid");
        _rulesPanel = Find<FrameworkElement>("RulesPanel");
        _summary = Find<TextBlock>("SummaryText");
        _search = Find<TextBox>("SearchBox");
        _changesOnly = Find<CheckBox>("ChangesOnly");
        _apply = Find<Button>("RenameButton");
        _token = Find<Button>("TokenButton");

        // Hub wording.
        Find<TextBlock>("TitleText").Text = "Rename & Renumber";
        Find<TextBlock>("DocText").Text = $"{shown.Select(r => r.Document).Distinct().Count()} file(s)";
        Find<TextBlock>("ScopeLabel").Text = "Rename";
        Find<TextBlock>("TargetsHint").Text = "Tick the values to change.";
        // Columns: include, current, new, kind, details, status.
        _grid.Columns[3].Header = "FIELD";
        _grid.Columns[4].Header = "FILE · ITEM";
        _grid.Columns[1].Header = "CURRENT";
        _grid.Columns[2].Header = "NEW";
        _changesOnly.Content = "Only values that change";
        _token.Content = "{ } Insert column value";
        _token.ToolTip = "Put another column's value, such as {Sheet Name} or {Discipline}, into the text box you were typing in. Each row gets its own value.";
        Find<TextBlock>("SearchHint").Text = "Search values";

        Choices(Find<ComboBox>("NameModeCombo"), (NameMode.Keep, "Keep the value"), (NameMode.Remove, "Start empty"), (NameMode.Fixed, "Fixed text:"), (NameMode.Reverse, "Reverse"));
        Choices(Find<ComboBox>("CaseModeCombo"), (CaseMode.Same, "Same"), (CaseMode.Upper, "UPPER CASE"), (CaseMode.Lower, "lower case"), (CaseMode.Title, "Title Case"), (CaseMode.Sentence, "Sentence case"));
        Choices(Find<ComboBox>("CropCombo"), (CropMode.None, "No crop"), (CropMode.Before, "Crop before"), (CropMode.After, "Crop after"));
        Choices(Find<ComboBox>("NumberModeCombo"), (NumberingMode.None, "No numbers"), (NumberingMode.Prefix, "At the start"), (NumberingMode.Suffix, "At the end"), (NumberingMode.Insert, "Insert at"), (NumberingMode.Replace, "Number only"));
        Choices(Find<ComboBox>("NumberStyleCombo"), (NumberingStyle.Decimal, "1, 2, 3"), (NumberingStyle.UpperLetters, "A, B, C"), (NumberingStyle.LowerLetters, "a, b, c"));

        // What to rename: the editable fields (Sheet Number, Sheet Name).
        foreach (var field in fields)
        {
            var choice = new FieldChoice(field, shown.Count(r => r.Values.ContainsKey(field.ColumnId)));
            choice.CheckedChanged += Reload;
            _fields.Add(choice);
        }
        var fieldsView = new ListCollectionView(_fields);
        fieldsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FieldChoice.Group)));
        Find<ListBox>("TargetsList").ItemsSource = fieldsView;

        _scope.ItemsSource = selected.Count > 1
            ? new[] { $"All {shown.Count} rows shown", $"The {selected.Count} selected rows" }
            : new[] { $"All {shown.Count} rows shown" };
        _scope.SelectedIndex = selected.Count > 1 ? 1 : 0;
        _scope.SelectionChanged += (_, _) => Reload();

        // Preview: numbering follows the list order (sort by clicking a column).
        _view = new ListCollectionView(_rows) { Filter = o => Visible((HubPreviewRow)o) };
        _grid.ItemsSource = _view;
        _grid.Sorting += (_, _) => _window.Dispatcher.BeginInvoke(new Action(ScheduleRefresh), DispatcherPriority.Background);
        var searchHint = Find<TextBlock>("SearchHint");
        _search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
        };
        _changesOnly.Checked += (_, _) => _view.Refresh();
        _changesOnly.Unchecked += (_, _) => _view.Refresh();
        Find<Button>("SelectAllButton").Click += (_, _) => SetIncluded(true);
        Find<Button>("SelectNoneButton").Click += (_, _) => SetIncluded(false);

        // Rules: any change refreshes the preview shortly after typing stops.
        _rulesPanel.DataContext = _rules;
        _refresh = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Refresh();
        };
        _rulesPanel.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is TextBox box) _lastText = box;
        }));
        // Typing in a panel, changing one of its choices or ticking one of its options turns the panel on.
        _rulesPanel.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox { IsKeyboardFocused: true } box) TurnPanelOn(box);
        }));
        _rulesPanel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox combo && (combo.IsKeyboardFocusWithin || combo.IsDropDownOpen)) TurnPanelOn(combo);
        }));
        _rulesPanel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is CheckBox option && !Equals(option.Tag, "On")) TurnPanelOn(option);
        }));

        _token.Click += (_, _) => ShowTokens();
        Find<Button>("ResetButton").Click += (_, _) => UseRules(new RenameRules());
        _apply.Click += (_, _) => Stage();
        Find<Button>("CloseButton").Click += (_, _) => _window.Close();
        Find<Button>("LoadPresetButton").Click += (_, _) => LoadPreset();
        Find<Button>("SavePresetButton").Click += (_, _) => SavePreset();
        Find<Button>("DeletePresetButton").Click += (_, _) => DeletePreset();
        _preset.ItemsSource = _presets.Names();
        _window.Closing += (_, _) => SaveLastRules();

        if (_fields.Count > 0) _fields[0].IsChecked = true; // Sheet Number
        Reload();
    }

    public void ShowDialog() => _window.ShowDialog();

    // ------------------------------------------------------------------ layout and theme

    private static Window LoadLayout()
    {
        var assembly = typeof(BulkRenameHubWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream("BulkRenameWindow.xaml")
                           ?? throw new InvalidOperationException("The Rename & Renumber layout is missing from Nexus.exe.");
        return (Window)XamlReader.Load(stream);
    }

    private static void ApplyTheme(Window window, bool dark)
    {
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["WindowBg"] = "#1B1B1C", ["PanelBg"] = "#242426", ["PanelAlt"] = "#29292B", ["StepBg"] = "#2A2A2D",
                ["InputBg"] = "#1E1E20", ["ButtonBg"] = "#2F2F32", ["Hover"] = "#38383C", ["Scroll"] = "#4A4A4F",
                ["Line"] = "#3A3A3E", ["LineStrong"] = "#55555B", ["Text"] = "#F2F2F2", ["TextDim"] = "#A3A3AA",
                ["Accent"] = "#3B82F6", ["AccentHover"] = "#5A96F8", ["AccentText"] = "#8AB8FF",
                ["Selection"] = "#1F3A5F", ["Good"] = "#6CCB5F", ["Bad"] = "#FF8A95", ["Warn"] = "#F5C451",
            }
            : new Dictionary<string, string>
            {
                ["WindowBg"] = "#F4F5F7", ["PanelBg"] = "#FFFFFF", ["PanelAlt"] = "#FAFAFB", ["StepBg"] = "#F7F8FA",
                ["InputBg"] = "#FFFFFF", ["ButtonBg"] = "#FFFFFF", ["Hover"] = "#EEF0F3", ["Scroll"] = "#C9CCD2",
                ["Line"] = "#E1E3E8", ["LineStrong"] = "#C4C8CF", ["Text"] = "#1B1C1F", ["TextDim"] = "#62666D",
                ["Accent"] = "#2563EB", ["AccentHover"] = "#1D4FD8", ["AccentText"] = "#1D4FD8",
                ["Selection"] = "#DCE8FD", ["Good"] = "#15803D", ["Bad"] = "#C42B1C", ["Warn"] = "#9D5D00",
            };
        foreach (var (key, hex) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            window.Resources[key] = brush;
        }
    }

    private void TurnPanelOn(DependencyObject field)
    {
        var rule = _window.TryFindResource("Rule") as Style;
        DependencyObject? d = field;
        while (d is not null && !(d is Border b && ReferenceEquals(b.Style, rule)))
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not Border panel) return;
        var on = Descendants(panel).OfType<CheckBox>().FirstOrDefault(c => Equals(c.Tag, "On"));
        if (on is not null && on.IsChecked != true) on.IsChecked = true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static void Choices<T>(ComboBox combo, params (T Value, string Label)[] items) where T : struct, Enum
    {
        combo.ItemsSource = items.Select(i => new KeyValuePair<T, string>(i.Value, i.Label)).ToList();
        combo.SelectedValuePath = "Key";
        combo.DisplayMemberPath = "Value";
        BindingOperations.GetBindingExpression(combo, Selector.SelectedValueProperty)?.UpdateTarget();
    }

    // ------------------------------------------------------------------ items and preview

    private IReadOnlyList<TableRow> ScopeRows => _scope.SelectedIndex == 1 ? _selected : _shown;

    private void Reload()
    {
        foreach (var r in _rows) r.IncludeChanged -= ScheduleRefresh;
        _rows.Clear();
        _sources.Clear();
        var columns = _main.TableColumns;
        int n = 0;
        foreach (var field in _fields.Where(f => f.IsChecked).Select(f => f.Field))
        {
            foreach (var row in ScopeRows)
            {
                if (!row.Values.ContainsKey(field.ColumnId)) continue;
                string key = (n++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _sources[key] = (row, field.ColumnId);
                var item = new RenameItem
                {
                    Key = key,
                    Current = _main.CurrentValue(row, field.ColumnId) ?? "",
                    Kind = field.Label,
                    Detail = $"{row.Document} · {row.Item.Trim()}",
                    Scope = field.Unique ? UniqueScope(row, field.ColumnId) : null,
                    Locked = Editing.Blocker(row, field.ColumnId),
                    Tokens = name => Token(row, name, columns),
                };
                var preview = new HubPreviewRow(item);
                preview.IncludeChanged += ScheduleRefresh;
                _rows.Add(preview);
            }
        }
        Refresh();
    }

    /// <summary>Sheet numbers are unique per file.</summary>
    private static string UniqueScope(TableRow row, string columnId) => row.Source.Result.DocumentId + "\u0001" + columnId;

    /// <summary>{Sheet Name}, {Discipline}... from the same row (edits not applied yet included); {File} and {Item} too.</summary>
    private string? Token(TableRow row, string name, IReadOnlyList<(string Id, string Label)> columns)
    {
        if (name.Equals("File", StringComparison.OrdinalIgnoreCase)) return row.Document;
        if (name.Equals("Item", StringComparison.OrdinalIgnoreCase)) return row.Item.Trim();
        foreach (var (id, label) in columns)
        {
            if (!label.Equals(name, StringComparison.OrdinalIgnoreCase) && !id.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!row.Values.ContainsKey(id)) continue;
            return _main.CurrentValue(row, id) ?? row.Values[id].Value?.ToString();
        }
        return null;
    }

    /// <summary>Values in use outside the list (other rows of the same files), so the preview flags clashes.</summary>
    private IEnumerable<(string Scope, string Name)> Others()
    {
        var listed = new HashSet<(TableRow, string)>(_sources.Values.Select(s => (s.Row, s.ColumnId)));
        foreach (var field in _fields.Where(f => f.IsChecked && f.Field.Unique).Select(f => f.Field))
            foreach (var row in _main.LoadedRows)
                if (row.Values.ContainsKey(field.ColumnId) && !listed.Contains((row, field.ColumnId))
                    && (_main.CurrentValue(row, field.ColumnId) ?? "").Trim() is { Length: > 0 } v)
                    yield return (UniqueScope(row, field.ColumnId), v);
    }

    private bool Visible(HubPreviewRow row)
    {
        if (_changesOnly.IsChecked == true && !row.Changes) return false;
        string search = _search.Text.Trim();
        return search.Length == 0
               || row.Current.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.New.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.Detail.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void SetIncluded(bool include)
    {
        foreach (HubPreviewRow row in _view) row.Include = include;
    }

    private void ScheduleRefresh()
    {
        _refresh.Stop();
        _refresh.Start();
    }

    /// <summary>New values for every row, in the list's current sort order (numbering follows it).</summary>
    private void Refresh()
    {
        var ordered = new ListCollectionView(_rows);
        foreach (var sort in _view.SortDescriptions) ordered.SortDescriptions.Add(sort);
        var rows = ordered.Cast<HubPreviewRow>().ToList();
        var byKey = rows.ToDictionary(r => r.Item.Key);

        _plan = RenamePlanner.Plan(rows.Select(r => r.Item).ToList(), _rules, i => byKey[i.Key].Include, Others().ToList());
        foreach (var result in _plan) byKey[result.Item.Key].Show(result);
        if (_changesOnly.IsChecked == true || _search.Text.Length > 0) _view.Refresh();

        int change = _plan.Count(p => p.WillRename);
        int bad = _plan.Count(p => p.Status is RenameStatus.Duplicate or RenameStatus.Invalid or RenameStatus.Empty);
        int locked = _plan.Count(p => p.Status == RenameStatus.Locked);
        _summary.Text = rows.Count == 0
            ? "Tick what to rename on the left."
            : string.Join("   ·   ", new[]
            {
                $"{change} of {rows.Count} will change",
                bad > 0 ? $"{bad} cannot (see Status)" : "",
                locked > 0 ? $"{locked} locked" : "",
                _rules.IsEmpty ? "Turn on a panel below to start" : "",
            }.Where(s => s.Length > 0));
        _summary.SetResourceReference(TextBlock.ForegroundProperty, bad > 0 ? "Bad" : "Text");
        _apply.IsEnabled = change > 0;
        _apply.Content = change > 0 ? $"Add {change} change{(change == 1 ? "" : "s")}" : "Add changes";
        _apply.ToolTip = "Added to your pending changes in the table. Nothing changes in the files until Review & apply.";
    }

    // ------------------------------------------------------------------ stage

    private void Stage()
    {
        Refresh();
        var changes = _plan.Where(p => p.WillRename).ToList();
        if (changes.Count == 0) return;
        int bad = _plan.Count(p => p.Status is RenameStatus.Duplicate or RenameStatus.Invalid or RenameStatus.Empty or RenameStatus.Locked);
        if (bad > 0 && MessageBox.Show(_window,
                $"{bad} value(s) cannot change (see Status) and stay as they are. Add the other {changes.Count}?",
                "Rename & Renumber", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        // Swaps and shifts (A101 → A102 while A102 → A103) are fine here: they are edits in the table, applied together.
        int staged = 0;
        var problems = new List<string>();
        foreach (var result in changes)
        {
            var (row, columnId) = _sources[result.Item.Key];
            var reason = _main.TrySetValue(row, columnId, result.New);
            if (reason is null) staged++;
            else problems.Add($"{result.Item.Current}: {reason}");
        }
        _main.Status = $"{staged} change{(staged == 1 ? "" : "s")} added to your pending changes. Review & apply to write them to the files."
                       + (problems.Count > 0 ? $" Not added: {string.Join("; ", problems.Take(3))}{(problems.Count > 3 ? "…" : "")}" : "");
        _window.Close();
    }

    // ------------------------------------------------------------------ tokens

    private void ShowTokens()
    {
        var menu = new ContextMenu { PlacementTarget = _token, Placement = PlacementMode.Top };
        menu.Items.Add(TokenItem("File"));
        menu.Items.Add(TokenItem("Item"));
        var used = new HashSet<string>(ScopeRows.SelectMany(r => r.Values.Keys), StringComparer.Ordinal);
        var columns = _main.TableColumns.Where(c => used.Contains(c.Id)).Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (columns.Count > 0)
        {
            menu.Items.Add(new Separator());
            var sheet = columns.Take(12).ToList();
            foreach (var c in sheet) menu.Items.Add(TokenItem(c));
            if (columns.Count > sheet.Count)
            {
                var more = new MenuItem { Header = "More columns" };
                foreach (var c in columns.Skip(sheet.Count)) more.Items.Add(TokenItem(c));
                menu.Items.Add(more);
            }
        }
        menu.IsOpen = true;
    }

    private MenuItem TokenItem(string name)
    {
        var item = new MenuItem { Header = "{" + name + "}" };
        item.Click += (_, _) => InsertToken("{" + name + "}");
        return item;
    }

    private void InsertToken(string token)
    {
        var box = _lastText;
        if (box is null)
        {
            _summary.Text = "Click in a text box of a panel first (Add › Prefix, Name › Fixed text…), then insert the column value.";
            return;
        }
        int at = Math.Min(box.CaretIndex, box.Text.Length);
        box.Text = box.Text.Insert(at, token);
        box.CaretIndex = at + token.Length;
        box.Focus();
    }

    // ------------------------------------------------------------------ presets (shared with Bulk Rename in Revit)

    private void UseRules(RenameRules rules)
    {
        _rules = rules;
        _rulesPanel.DataContext = _rules;
        Refresh();
    }

    private void LoadPreset()
    {
        string name = _preset.Text.Trim();
        var rules = name.Length > 0 ? _presets.Load(name) : null;
        if (rules is null)
        {
            _summary.Text = "Pick a saved preset to load.";
            return;
        }
        UseRules(rules);
        _summary.Text = $"Loaded preset \"{name}\".";
    }

    private void SavePreset()
    {
        string name = _preset.Text.Trim();
        if (name.Length == 0)
        {
            _summary.Text = "Type a name for the preset in the Preset box, then Save.";
            return;
        }
        _presets.Save(name, _rules);
        _preset.ItemsSource = _presets.Names();
        _preset.Text = name;
        _summary.Text = $"Saved preset \"{name}\".";
    }

    private void DeletePreset()
    {
        string name = _preset.Text.Trim();
        if (name.Length == 0) return;
        _presets.Delete(name);
        _preset.ItemsSource = _presets.Names();
        _preset.Text = "";
        _summary.Text = $"Deleted preset \"{name}\".";
    }

    private static string LastRulesPath => Path.GetFullPath(Path.Combine(RenamePresets.DefaultFolder, "..", LastRulesFile));

    private static RenameRules LoadLastRules()
    {
        try
        {
            if (File.Exists(LastRulesPath)) return RenamePresets.FromJson(File.ReadAllText(LastRulesPath));
        }
        catch { /* start fresh */ }
        return new RenameRules();
    }

    private void SaveLastRules()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastRulesPath)!);
            File.WriteAllText(LastRulesPath, RenamePresets.ToJson(_rules));
        }
        catch { /* the next session starts from the defaults */ }
    }
}

/// <summary>One field in "What to rename" (Sheet Number, Sheet Name).</summary>
internal sealed class FieldChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public FieldChoice(RenameField field, int count)
    {
        Field = field;
        Count = count;
    }

    public RenameField Field { get; }
    public string Group => "Sheets";
    public string Label => Field.Label;
    public string Tip => Field.Unique ? "Unique in each file: clashes are flagged before anything changes." : "";
    public int Count { get; }
    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke();
        }
    }
}

/// <summary>One preview line: current value → new value, and what will happen.</summary>
internal sealed class HubPreviewRow : INotifyPropertyChanged
{
    private bool _include = true;
    private string _new;
    private string _status = "";
    private string _tone = "";
    private string? _message;

    public HubPreviewRow(RenameItem item)
    {
        Item = item;
        _new = item.Current;
    }

    public RenameItem Item { get; }
    public string Current => Item.Current;
    public string Kind => Item.Kind;
    public string Detail => Item.Detail;
    public bool Changes { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? IncludeChanged;

    public bool Include
    {
        get => _include;
        set
        {
            if (Set(ref _include, value)) IncludeChanged?.Invoke();
        }
    }

    public string New { get => _new; private set => Set(ref _new, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>"Good", "Bad", "Warn" or "" (colour of the status).</summary>
    public string Tone { get => _tone; private set => Set(ref _tone, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }

    public void Show(RenameResult result)
    {
        New = result.Status is RenameStatus.Skipped ? "" : result.New;
        Changes = result.Status != RenameStatus.Unchanged && result.Status != RenameStatus.Skipped;
        (Status, Tone) = result.Status switch
        {
            RenameStatus.Rename => (result.Message is null ? "Will change" : "Will change · " + result.Message, result.Message is null ? "Good" : "Warn"),
            RenameStatus.Unchanged => ("No change", ""),
            RenameStatus.Skipped => ("Not included", ""),
            RenameStatus.Duplicate => ("Already used", "Bad"),
            RenameStatus.Invalid => ("Not allowed", "Bad"),
            RenameStatus.Empty => ("Empty", "Bad"),
            RenameStatus.Locked => ("Locked", "Warn"),
            _ => (result.Status.ToString(), ""),
        };
        Message = result.Message;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
