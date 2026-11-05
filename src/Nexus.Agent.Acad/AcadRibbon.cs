using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcUiApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Nexus.Agent.Acad;

/// <summary>
/// "Nexus" ribbon tab in AutoCAD / Civil 3D / Plant 3D with one button that shows the hub
/// connection state (like the Revit one). AutoCAD-based programs rebuild the ribbon from the
/// workspace during startup, on workspace switches and CUI reloads, which drops added tabs; so the
/// tab is checked on Idle (at most every 2 s) and added again whenever it is missing.
/// Status changes arrive on background threads and are applied on AutoCAD's Idle event.
/// </summary>
internal sealed class AcadRibbon : IDisposable
{
    private const string TabId = "NEXUS_TAB";

    private readonly AgentLog _log;
    private readonly Func<AgentStatus?> _status;
    private readonly Func<HostInfo?> _host;
    private readonly Dictionary<AgentState, ImageSource> _large = new();
    private readonly Dictionary<AgentState, ImageSource> _small = new();
    private RibbonButton? _button;
    private AgentState _state = AgentState.Stopped;
    private volatile AgentStatus? _pending;
    private readonly Stopwatch _sinceCheck = Stopwatch.StartNew();
    private bool _loggedAdded;

    public AcadRibbon(AgentLog log, Func<AgentStatus?> status, Func<HostInfo?> host)
    {
        _log = log;
        _status = status;
        _host = host;
        BuildIcons();

        if (ComponentManager.Ribbon is not null) Create();
        AcApp.SystemVariableChanged += OnSystemVariableChanged;
        AcApp.Idle += OnIdle;
    }

    /// <summary>Called from any thread.</summary>
    public void OnStatusChanged(AgentStatus status) => _pending = status;

    private void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
    {
        // Switching workspace rebuilds the ribbon without our tab.
        if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) Create();
        // Dark/light interface: switch between the white and black N.
        if (string.Equals(e.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase))
        {
            BuildIcons();
            ApplyStatus(_status());
        }
    }

    /// <summary>White N on AutoCAD's dark theme (COLORTHEME 0), black N on the light theme.</summary>
    private void BuildIcons()
    {
        bool dark = true;
        try { dark = Convert.ToInt32(AcApp.GetSystemVariable("COLORTHEME")) == 0; } catch { /* default: dark */ }
        foreach (AgentState s in Compat.EnumValues<AgentState>())
        {
            _large[s] = RibbonIcons.Create(s, 32, dark);
            _small[s] = RibbonIcons.Create(s, 16, dark);
        }
    }

    private void Create()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon is null || ribbon.FindTab(TabId) is not null) return;

            _button = new RibbonButton
            {
                Id = "NEXUS_STATUS",
                Text = Label(_state),
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                LargeImage = _large[_state],
                Image = _small[_state],
                ToolTip = "Nexus agent status. Click for details or to show the Nexus hub.",
                CommandHandler = new ShowStatusHandler(this),
            };
            var source = new RibbonPanelSource { Title = "Hub Link" };
            source.Items.Add(_button);
            var tab = new RibbonTab { Title = "Nexus", Id = TabId };
            tab.Panels.Add(new RibbonPanel { Source = source });
            ribbon.Tabs.Add(tab);
            ApplyStatus(_status());
            if (!_loggedAdded)
            {
                _loggedAdded = true;
                _log.Info("Nexus ribbon tab added.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Could not create the Nexus ribbon tab.", ex);
        }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        // The ribbon appears late and is rebuilt now and then: put the tab back when it is missing.
        if (_sinceCheck.ElapsedMilliseconds > 2000)
        {
            _sinceCheck.Restart();
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon is not null && ribbon.FindTab(TabId) is null) Create();
            }
            catch (Exception ex)
            {
                _log.Warn("Checking the Nexus ribbon tab failed.", ex);
            }
        }

        var status = _pending;
        if (status is null) return;
        _pending = null;
        ApplyStatus(status);
    }

    private void ApplyStatus(AgentStatus? status)
    {
        if (status is null) return;
        _state = status.State;
        if (_button is null) return;
        try
        {
            _button.Text = Label(status.State);
            _button.LargeImage = _large[status.State];
            _button.Image = _small[status.State];
        }
        catch (Exception ex)
        {
            _log.Warn("Could not update ribbon status.", ex);
        }
    }

    private static string Label(AgentState state) => state switch
    {
        AgentState.Connected => "Hub\nConnected",
        AgentState.Listening => "Hub\nWaiting",
        AgentState.Faulted => "Hub\nError",
        _ => "Hub\nStarting",
    };

    /// <summary>Status window: state, pipe, host, log; show the hub; open the log folder.</summary>
    public void ShowStatusWindow()
    {
        var s = _status();
        var host = _host();
        string text = s is null
            ? "The Nexus agent is not running. See the log for details."
            : $"State: {s.State}\n" +
              $"Host: {host?.Name}{(host?.Modules.Count > 0 ? "  (modules: " + string.Join(", ", host.Modules) + ")" : "")}\n" +
              $"Pipe: {s.PipeName}\n" +
              $"Hub connections: {s.Clients}\n" +
              $"Requests handled: {s.RequestsHandled}\n" +
              (s.LastError is null ? "" : $"Last error: {s.LastError}\n") +
              $"Log: {_log.FilePath}";

        var window = new Window
        {
            Title = "Nexus Agent",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = s is null ? "Agent is not running" : $"Agent is {s.State}",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x8C)),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = text, MaxWidth = 520, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Nexus runs in the background (tray icon next to the clock).",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 10, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button MakeButton(string label, Action onClick)
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => onClick();
            buttons.Children.Add(b);
            return b;
        }
        MakeButton("Show the Nexus hub", () =>
        {
            window.Close();
            var error = HubControl.Show(_log);
            if (error is not null) MessageBox.Show(error, "Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
        }).IsDefault = true;
        MakeButton("Open log folder", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NexusPaths.LogsDir}\"") { UseShellExecute = true }));
        MakeButton("Close", window.Close).IsCancel = true;
        panel.Children.Add(buttons);
        window.Content = panel;

        AcUiApp.ShowModalWindow(window);
    }

    public void Dispose()
    {
        try
        {
            AcApp.SystemVariableChanged -= OnSystemVariableChanged;
            AcApp.Idle -= OnIdle;
        }
        catch { /* shutting down */ }
    }

    private sealed class ShowStatusHandler : ICommand
    {
        private readonly AcadRibbon _ribbon;
        public ShowStatusHandler(AcadRibbon ribbon) => _ribbon = ribbon;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            try { _ribbon.ShowStatusWindow(); }
            catch (Exception ex) { _ribbon._log.Warn("Could not show the status window.", ex); }
        }
    }
}
