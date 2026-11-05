using System.Windows;
using System.Windows.Threading;
using Nexus.Hub.Core;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>
/// Nexus runs in the background like Autodesk Access: one instance per user, a tray
/// icon, started at sign-in with --background. Closing the window only hides it.
///
/// Command line:
///   --background   start hidden in the tray (used by Start with Windows)
///   --replace      stop the running hub and take over (Visual Studio F5)
///   --install      install this Nexus.exe to %LOCALAPPDATA%\Nexus\Hub (Start Menu, Settings › Apps,
///                  start with Windows) and start it; add --background to start it in the tray only
///   --uninstall    stop the hub and remove it (also what Settings › Apps › Uninstall runs)
///   --shutdown     ask the running hub to exit
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan HostPollInterval = TimeSpan.FromSeconds(3);

    private HubInstance? _instance;
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private DispatcherTimer? _poll;
    private bool _exiting;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        // --dataset <reader id>: open that view (from a program's ribbon, e.g. Fabrication parts).
        int datasetArg = Array.FindIndex(e.Args, a => a.Equals("--dataset", StringComparison.OrdinalIgnoreCase));
        string? dataset = datasetArg >= 0 && datasetArg + 1 < e.Args.Length ? e.Args[datasetArg + 1] : null;

        if (args.Contains("--install"))
        {
            int code;
            try
            {
                code = HubInstance.Install(background: args.Contains("--background"));
            }
            catch (Exception ex)
            {
                // Report and exit with an error code: the install scripts check it (and an exception here would
                // otherwise leave this process running with no window).
                HubLog.Error("Install failed.", ex);
                Console.Error.WriteLine("Install failed: " + ex.Message);
                MessageBox.Show("Nexus could not be installed:\n\n" + ex.Message + "\n\nDetails are in the log: " + HubLog.FilePath,
                    "Install Nexus", MessageBoxButton.OK, MessageBoxImage.Error);
                code = 1;
            }
            Shutdown(code);
            return;
        }
        if (args.Contains("--uninstall"))
        {
            bool quiet = args.Contains("--quiet");
            if (!quiet && MessageBox.Show("Remove Nexus from this computer?\n\nThe Revit and AutoCAD add-ins are not affected; they are removed separately.",
                    "Uninstall Nexus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Shutdown(1);
                return;
            }
            int code = HubInstance.Uninstall();
            if (!quiet) MessageBox.Show("Nexus has been removed.", "Uninstall Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(code);
            return;
        }
        if (args.Contains("--shutdown"))
        {
            HubInstance.Send("exit");
            Shutdown(0);
            return;
        }

        _instance = HubInstance.Claim();
        if (!_instance.IsOwner)
        {
            if (args.Contains("--replace"))
            {
                HubInstance.Send("exit");
                if (!_instance.WaitForOwnership(TimeSpan.FromSeconds(15)))
                {
                    MessageBox.Show("The running Nexus hub did not exit.", "Nexus", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(1);
                    return;
                }
            }
            else
            {
                // Already running: bring it forward (unless this is the sign-in start) and leave.
                if (!args.Contains("--background")) HubInstance.Send(dataset is null ? "show" : "dataset " + dataset);
                _instance.Dispose();
                Shutdown(0);
                return;
            }
        }

        // Windows 11 look (rounded controls, Mica, system accent colour); light/dark follows Windows unless
        // a theme was picked in the hub (⋯ › Theme).
        AppTheme.Apply(UiSettings.Load().Theme);

        HubLog.Info($"Hub started ({(args.Contains("--background") ? "background" : "window")}). Log: {HubLog.FilePath}");
        StartupRegistration.Apply();

        _vm = new MainViewModel();
        _window = new MainWindow(_vm);
        _window.Closing += (_, ce) =>
        {
            if (_exiting) return;
            ce.Cancel = true;
            _window.Hide();
            _tray?.NotifyStillRunning();
        };

        _tray = new TrayIcon(ShowWindow, () => _vm.RefreshCommand.Execute(null), () => ExitHub(ask: true));
        _vm.HostsChanged += summary => _tray?.SetStatus(summary);

        _instance.Listen(command => Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case "show": ShowWindow(); break;
                case "refresh": _vm.RefreshCommand.Execute(null); break;
                case "exit": ExitHub(ask: false); break;
                case var c when c.StartsWith("dataset ", StringComparison.Ordinal):
                    ShowWindow();
                    _ = _vm.ShowReaderAsync(c["dataset ".Length..].Trim());
                    break;
            }
        }));

        // Pick up hosts as they start and stop, even while the window is hidden.
        _poll = new DispatcherTimer { Interval = HostPollInterval };
        _poll.Tick += async (_, _) => await _vm.RefreshIfHostsChangedAsync();
        _poll.Start();
        _ = _vm.StartAsync();

        if (!args.Contains("--background")) ShowWindow();
        if (dataset is not null) _ = _vm.ShowReaderAsync(dataset);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Windows may refuse to focus a background process; a Topmost flip brings it forward anyway.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitHub(bool ask)
    {
        if (ask && _vm is not null && !_vm.ConfirmExit()) return;
        _exiting = true;
        _poll?.Stop();
        // Close the program connections cleanly before the process ends.
        foreach (var agent in _vm?.Agents.ToList() ?? new())
        {
            try { agent.Connection.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); } catch { /* ignored */ }
        }
        _window?.Close();
        Shutdown(0);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        HubLog.Info("Hub exited.");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HubLog.Error("Unhandled error", e.Exception);
        // While closing, background work stopping late is expected: log it, never show a dialog.
        if (_exiting || e.Exception is ObjectDisposedException or OperationCanceledException)
        {
            e.Handled = true;
            return;
        }
        // Shown in the hub's info bar (no pop-up); the full error is in the log.
        if (_vm is not null) _vm.Notify(NoticeKind.Error, "Something went wrong", e.Exception.Message + "  (Details are in the log: ⋯ › Messages and log.)");
        else Dialogs.Message("Something went wrong", e.Exception.Message, NoticeKind.Error);
        e.Handled = true;
    }
}
