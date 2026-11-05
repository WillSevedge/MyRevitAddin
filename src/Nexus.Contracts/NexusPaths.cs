namespace Nexus.Contracts;

/// <summary>
/// Well-known folders. Everything lives under %LOCALAPPDATA%\Nexus unless the
/// NEXUS_HOME environment variable overrides it (used by tests).
/// </summary>
public static class NexusPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("NEXUS_HOME");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus");
        }
    }

    public static string AgentsDir => Path.Combine(Root, "agents");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ExportsDir => Path.Combine(Root, "exports");

    /// <summary>Where the build installs the hub; Windows starts it from here at sign-in.</summary>
    public static string HubInstallDir => Path.Combine(Root, "Hub");
    public static string HubExe => Path.Combine(HubInstallDir, "Nexus.exe");
    public static string HubSettingsFile => Path.Combine(Root, "hub-settings.json");

    /// <summary>Print &amp; PDF: the copies of drawings AutoCAD's Core Console plots (the original is never opened).</summary>
    public static string OfflineWorkDir => Path.Combine(Root, "offline", "work");

    public static string RegistrationFile(int processId) => Path.Combine(AgentsDir, $"{processId}.json");
}
