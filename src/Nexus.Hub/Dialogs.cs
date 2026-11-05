using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus.Hub;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>Small code-built dialogs: ask for a value, find and replace, confirmations, and the messages window.</summary>
internal static class Dialogs
{
    /// <summary>The window to put a dialog over: the active one, else the hub.</summary>
    public static Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);

    /// <summary>A question with two answers, in the hub's style. True when the first (accent) button was chosen.</summary>
    public static bool Confirm(string title, string message, string confirmText, string cancelText = "Cancel",
        NoticeKind kind = NoticeKind.Warning, Window? owner = null)
    {
        bool ok = false;
        var window = Card(owner, title, message, kind, out var buttons);
        var yes = new Button { Content = confirmText, IsDefault = true, MinWidth = 96, Padding = new Thickness(14, 5, 14, 5) };
        yes.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        yes.Click += (_, _) => { ok = true; window.DialogResult = true; };
        var no = new Button { Content = cancelText, IsCancel = true, MinWidth = 96, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        window.ShowDialog();
        return ok;
    }

    /// <summary>A message that needs acknowledging (used where an info bar would not be seen, e.g. over another dialog).</summary>
    public static void Message(string title, string message, NoticeKind kind = NoticeKind.Info, Window? owner = null)
    {
        var window = Card(owner, title, message, kind, out var buttons);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 96, Padding = new Thickness(14, 5, 14, 5) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        window.ShowDialog();
    }

    private static Window Card(Window? owner, string title, string message, NoticeKind kind, out StackPanel buttons)
    {
        owner ??= ActiveWindow;
        var window = new Window
        {
            Title = "Nexus",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
        };
        if (owner is not null) window.Owner = owner;

        var (glyph, brush) = kind switch
        {
            NoticeKind.Success => ("\uE930", "SystemFillColorSuccessBrush"),
            NoticeKind.Warning => ("\uE7BA", "SystemFillColorCautionBrush"),
            NoticeKind.Error => ("\uEA39", "SystemFillColorCriticalBrush"),
            _ => ("\uE946", "SystemFillColorAttentionBrush"),
        };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);

        var text = new StackPanel { MaxWidth = 460 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MinWidth = 360 };
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        window.Content = panel;
        return window;
    }

    public static string? AskValue(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 380, Padding = new Thickness(4, 3, 4, 3) };
        var window = Shell(owner, title, prompt, box, out var ok);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true && ok() ? box.Text : null;
    }

    public sealed record FindReplace(string Find, string Replace, bool MatchCase, bool WholeCell, bool SelectionOnly);

    public static FindReplace? AskFindReplace(Window owner, bool hasSelection)
    {
        var find = new TextBox { MinWidth = 320, Padding = new Thickness(4, 3, 4, 3) };
        var replace = new TextBox { MinWidth = 320, Padding = new Thickness(4, 3, 4, 3) };
        var matchCase = new CheckBox { Content = "Match case", Margin = new Thickness(0, 8, 0, 0) };
        var whole = new CheckBox { Content = "Whole cell only", Margin = new Thickness(0, 4, 0, 0) };
        var selection = new CheckBox { Content = "Only in the selected cells", IsChecked = hasSelection, IsEnabled = hasSelection, Margin = new Thickness(0, 4, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Find" });
        panel.Children.Add(find);
        panel.Children.Add(new TextBlock { Text = "Replace with", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(replace);
        panel.Children.Add(matchCase);
        panel.Children.Add(whole);
        panel.Children.Add(selection);
        var window = Shell(owner, "Find and replace", "Replaces text in editable cells; the changes are pending until you apply them.", panel, out var ok);
        window.Loaded += (_, _) => find.Focus();
        if (window.ShowDialog() != true || !ok() || find.Text.Length == 0) return null;
        return new FindReplace(find.Text, replace.Text, matchCase.IsChecked == true, whole.IsChecked == true, selection.IsChecked == true);
    }

    public static void ShowMessages(Window owner, IEnumerable<string> issues, IEnumerable<string> log, IEnumerable<string>? editing = null)
    {
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Last load", Content = new ListBox { ItemsSource = issues.DefaultIfEmpty("No errors or warnings.").ToList() } });
        if (editing is not null)
            tabs.Items.Add(new TabItem { Header = "Editing", Content = new ListBox { ItemsSource = editing.ToList(), FontFamily = new FontFamily("Consolas") } });
        tabs.Items.Add(new TabItem { Header = "Log", Content = new ListBox { ItemsSource = log.Reverse().ToList(), FontFamily = new FontFamily("Consolas") } });
        new Window
        {
            Title = "Nexus - Messages",
            Owner = owner,
            Width = 900,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(10), Child = tabs },
        }.ShowDialog();
    }

    private static Window Shell(Window owner, string title, string prompt, UIElement body, out Func<bool> accepted)
    {
        bool ok = false;
        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        okButton.Click += (_, _) => { ok = true; window.DialogResult = true; };
        buttons.Children.Add(okButton);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        window.Content = panel;
        accepted = () => ok;
        return window;
    }
}
