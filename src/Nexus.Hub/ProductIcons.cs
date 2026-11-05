using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>
/// The real Autodesk product icons, read from what is installed on this PC (nothing is shipped
/// with Nexus): first the product's Start Menu shortcut (Civil 3D's shortcut has the Civil 3D
/// icon although it starts acad.exe), then the running program's own executable.
/// </summary>
internal static class ProductIcons
{
    private const int Size = 48;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Civil 3D 2026", "Plant 3D 2026", "Revit 2026", "AutoCAD 2026".</summary>
    public static ImageSource? For(HostInfo host, string product)
    {
        string key = $"{product} {host.Version}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        ImageSource? icon = null;
        try
        {
            icon = FromStartMenu(product, host.Version) ?? FromProcess(host.ProcessId);
        }
        catch (Exception ex)
        {
            HubLog.Warn($"Could not read the {key} icon.", ex);
        }

        lock (Cache) Cache[key] = icon;
        return icon;
    }

    private static ImageSource? FromStartMenu(string product, string version)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        // e.g. "Civil 3D 2026 - English.lnk", "AutoCAD Plant 3D 2026 - English.lnk", "Revit 2026.lnk", "AutoCAD 2026 - English.lnk"
        var shortcuts = roots.Where(Directory.Exists)
            .SelectMany(r => SafeEnumerate(r, "*.lnk"))
            .Where(f => Path.GetFileNameWithoutExtension(f).Contains($"{product} {version}", StringComparison.OrdinalIgnoreCase))
            // Prefer the plain product shortcut over "... Safe Mode", "Reset settings", viewers etc.
            .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length)
            .ToList();
        if (product == "AutoCAD")
            shortcuts.RemoveAll(f => !Path.GetFileNameWithoutExtension(f).StartsWith("AutoCAD", StringComparison.OrdinalIgnoreCase));

        foreach (var lnk in shortcuts)
        {
            var (file, index) = ShortcutIcon(lnk);
            if (file is null) continue;
            var image = Extract(file, index);
            if (image is not null) return image;
        }
        return null;
    }

    private static ImageSource? FromProcess(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            string? exe = p.MainModule?.FileName;
            return exe is null ? null : Extract(exe, 0);
        }
        catch
        {
            return null; // exited, or no access
        }
    }

    private static (string? File, int Index) ShortcutIcon(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (null, 0);
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            string location = link.IconLocation ?? "";
            string target = link.TargetPath ?? "";

            // IconLocation is "path,index"; ",0" (no path) means: the target's own icon.
            string file = target;
            int index = 0;
            int comma = location.LastIndexOf(',');
            string path = comma >= 0 ? location[..comma] : location;
            if (comma >= 0) int.TryParse(location[(comma + 1)..], out index);
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (path.Length > 0 && File.Exists(path)) file = path;
            return File.Exists(file) ? (file, index) : (null, 0);
        }
        catch
        {
            return (null, 0);
        }
    }

    private static ImageSource? Extract(string file, int index)
    {
        var icons = new IntPtr[1];
        var ids = new int[1];
        // PrivateExtractIcons picks the best image at the requested size (sharp in the sidebar).
        int n = PrivateExtractIcons(file, index, Size, Size, icons, ids, 1, 0);
        if (n <= 0 || icons[0] == IntPtr.Zero)
        {
            if (index != 0) return Extract(file, 0);
            return null;
        }
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, int count, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
