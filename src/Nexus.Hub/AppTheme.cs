using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Nexus.Hub;

/// <summary>
/// Light or dark: follows Windows, or a choice saved in the hub settings. Every window (the hub, its dialogs and
/// the windows built in code) takes its text colour from the theme: the Windows 11 theme colours controls but
/// leaves a window's own text at the system default (black), which is unreadable on the dark background.
/// </summary>
internal static class AppTheme
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static bool _hooked;
    private static string _choice = System;
    private static ResourceDictionary? _palette;

    /// <summary>The Nexus colours in use are the dark ones.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the light/dark palette changed (code that picked colours itself refreshes them).</summary>
    public static event Action? Changed;

    /// <summary>Applies the theme (null or unknown: follow Windows) and makes sure window text follows it.</summary>
    public static void Apply(string? choice)
    {
        if (!_hooked)
        {
            _hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            // "Use Windows setting": follow Windows when it switches between light and dark.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _choice == System)
                    Application.Current?.Dispatcher.BeginInvoke(() => ApplyPalette(WindowsUsesDark()));
            };
        }
        _choice = Normalize(choice);
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10
        Application.Current.ThemeMode = choice switch
        {
            Light => ThemeMode.Light,
            Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        ApplyPalette(_choice == Dark || (_choice == System && WindowsUsesDark()));
        foreach (Window window in Application.Current.Windows) FollowTheme(window);
    }

    /// <summary>Puts the light or dark Nexus palette (Themes/Palette.*.xaml) in the application resources.</summary>
    private static void ApplyPalette(bool dark)
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        _palette ??= resources.FirstOrDefault(d => d.Source?.OriginalString.Contains("Palette.", StringComparison.Ordinal) == true);
        if (_palette is not null && IsDark == dark && resources.Contains(_palette)) return;
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Nexus;component/Themes/Palette.{(dark ? Dark : Light)}.xaml") };
        int index = _palette is null ? -1 : resources.IndexOf(_palette);
        if (index >= 0) resources[index] = next;
        else resources.Add(next);
        _palette = next;
        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>Windows "Choose your app mode": dark (AppsUseLightTheme = 0).</summary>
    private static bool WindowsUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(string? choice) => choice is Light or Dark ? choice : System;

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) FollowTheme(window);
    }

    private static void FollowTheme(Window window)
    {
        // Only when the window did not pick its own colour. A resource reference follows later theme changes.
        if (window.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
    }
}
