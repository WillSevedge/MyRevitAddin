using System.Drawing;
using WinForms = System.Windows.Forms;

namespace Nexus.Hub;

/// <summary>The Nexus icon next to the clock: open the hub, refresh, start with Windows, exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _startWithWindows;
    private Icon _image;
    private bool _toldAboutBackground;

    public TrayIcon(Action open, Action refresh, Action exit)
    {
        _image = CreateIcon();
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Nexus", null, (_, _) => open()) { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) };
        _startWithWindows = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool on = !_startWithWindows!.Checked;
            StartupRegistration.Set(on);
            _startWithWindows.Checked = on;
        }) { Checked = StartupRegistration.IsEnabled };

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Refresh hosts", null, (_, _) => refresh()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit Nexus", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = "Nexus",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // The taskbar can be dark or light: use the white or black N to match, and follow changes.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
        };
    }

    /// <summary>Tooltip text (Windows limits it to 127 characters).</summary>
    public void SetStatus(string text)
    {
        text = "Nexus - " + text;
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>The first time the window is closed, explain that Nexus keeps running.</summary>
    public void NotifyStillRunning()
    {
        if (_toldAboutBackground) return;
        _toldAboutBackground = true;
        _icon.ShowBalloonTip(4000, "Nexus is still running",
            "Nexus stays in the background to connect Revit, AutoCAD, Civil 3D and Plant 3D. Click the tray icon to open it, or right-click it to exit.",
            WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>The Nexus N for the notification area, white on a dark taskbar and black on a light one.</summary>
    private static Icon CreateIcon()
    {
        string name = TaskbarIsDark() ? "Nexus-tray-white.ico" : "Nexus-tray-black.ico";
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name)
                           ?? typeof(TrayIcon).Assembly.GetManifestResourceStream("Nexus.ico")
                           ?? throw new InvalidOperationException("The tray icon is not embedded.");
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    private static bool TaskbarIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme: 1 = light taskbar/Start, 0 = dark (also the default on Windows 10 and 11).
            return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
        try
        {
            var next = CreateIcon();
            var old = _image;
            _icon.Icon = next;
            _image = next;
            old.Dispose();
        }
        catch
        {
            // Keep the current icon.
        }
    }
}
