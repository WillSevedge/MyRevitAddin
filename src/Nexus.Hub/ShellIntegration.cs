using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>
/// Makes the installed hub a normal Windows program for the current user: a Start Menu
/// shortcut and an entry in Settings › Apps (with Uninstall). No administrator rights needed.
/// </summary>
internal static class ShellIntegration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexus";

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nexus.lnk");

    public static void Register()
    {
        string exe = NexusPaths.HubExe;
        try
        {
            CreateShortcut(StartMenuShortcut, exe, "Nexus hub: view and edit Revit, AutoCAD, Civil 3D and Plant 3D data");
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not create the Start Menu shortcut.", ex);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Nexus");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "Nexus");
            key.SetValue("InstallLocation", NexusPaths.HubInstallDir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirectorySize(NexusPaths.HubInstallDir) / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not register Nexus in Settings > Apps.", ex);
        }
    }

    /// <summary>
    /// Windows caches program icons by path, so after an update the taskbar and Start Menu can keep
    /// showing the old icon. Tell the shell icons changed and rebuild its icon cache.
    /// </summary>
    public static void RefreshIcons()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
            string ie4uinit = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
            if (File.Exists(ie4uinit))
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ie4uinit, "-show")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p?.WaitForExit(10000);
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not refresh the Windows icon cache.", ex);
        }
    }

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Unregister()
    {
        try { File.Delete(StartMenuShortcut); } catch { /* ignored */ }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { /* ignored */ }
    }

    private static string Version =>
        typeof(ShellIntegration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    private static void CreateShortcut(string path, string target, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    private static long DirectorySize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
