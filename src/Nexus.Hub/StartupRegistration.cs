using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>"Start with Windows": a per-user Run entry that starts the installed hub in the background.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nexus";

    private sealed class Settings
    {
        public bool StartWithWindows { get; set; } = true;
    }

    public static bool IsEnabled => Load().StartWithWindows;

    /// <summary>Called at hub start: applies the saved choice (on by default) and keeps the path current.</summary>
    public static void Apply() => Set(Load().StartWithWindows);

    public static void Set(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(NexusPaths.Root);
            File.WriteAllText(NexusPaths.HubSettingsFile, JsonSerializer.Serialize(new Settings { StartWithWindows = enabled }));

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                // Prefer the installed copy, so a Debug build folder is never what Windows starts.
                string exe = File.Exists(NexusPaths.HubExe) ? NexusPaths.HubExe : Environment.ProcessPath ?? NexusPaths.HubExe;
                key.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not update the Start with Windows setting.", ex);
        }
    }

    /// <summary>Removes the Run entry without changing the saved choice (used by uninstall).</summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not remove the startup entry.", ex);
        }
    }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(NexusPaths.HubSettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(NexusPaths.HubSettingsFile)) ?? new Settings();
        }
        catch
        {
            // Corrupt settings: fall back to defaults.
        }
        return new Settings();
    }
}
