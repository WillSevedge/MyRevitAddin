using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit.Fabrication;

/// <summary>The Revit ↔ Fabrication CADmep bridge actions behind the Fabrication ribbon panel and window.</summary>
internal static class FabricationActions
{
    /// <summary>
    /// Pulls changes made to the fabrication database (in CADmep/ESTmep) into the model.
    /// Returns a summary for the user.
    /// </summary>
    public static string Reload(Document doc)
    {
        var config = FabricationDatabase.Configured(doc) ?? throw new InvalidOperationException(FabricationDatabase.NotConfigured);
        using var t = new Transaction(doc, "Nexus: reload fabrication configuration");
        if (t.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit could not start a transaction. Finish the current command and try again.");
        ConfigurationReloadInfo info;
        try
        {
            info = config.ReloadConfiguration();
            t.Commit();
        }
        catch
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            throw;
        }

        var lines = new List<string>
        {
            $"Parts that were out of date: {info.OutOfDatePartCount}",
            $"Parts whose custom data changed: {SafeCount(() => info.GetCustomDataChangedElements().Count)}",
            $"Disconnections caused by the reload: {info.Disconnects}",
        };
        if (info.ProfileNotAvailable) lines.Add("Warning: the model's profile is not in the database on disk any more.");
        return string.Join("\n", lines);
    }

    /// <summary>Fabrication parts to export: the selection (parts, assemblies, groups), else every part in the active view.</summary>
    public static (HashSet<ElementId> Ids, string Scope) PartsToExport(UIDocument uidoc)
    {
        var doc = uidoc.Document;
        var selected = uidoc.Selection.GetElementIds().ToHashSet();
        if (selected.Count > 0) return (selected, $"{selected.Count} selected element(s)");

        var view = doc.ActiveView;
        var ids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FabricationPart)).ToElementIds().ToHashSet();
        return (ids, $"{ids.Count} fabrication part(s) in the view '{view.Name}'");
    }

    /// <summary>Writes a MAJ job that Fabrication CADmep/ESTmep/CAMduct open. Returns a summary for the user.</summary>
    public static string ExportMaj(Document doc, ISet<ElementId> ids, string path, bool holesForTaps)
    {
        if (FabricationDatabase.Configured(doc) is null) throw new InvalidOperationException(FabricationDatabase.NotConfigured);
        if (ids.Count == 0) throw new InvalidOperationException("There are no fabrication parts to export. Select parts (or open a view that shows them) and try again.");
        var result = FabricationPart.SaveAsFabricationJob(doc, ids, path, new FabricationSaveJobOptions(holesForTaps));
        int parts = ids.Count(id => doc.GetElement(id) is FabricationPart);
        return File.Exists(path)
            ? $"Saved {parts.ToString(CultureInfo.CurrentCulture)} part(s) to\n{path}\n\nOpen it in Fabrication CADmep, ESTmep or CAMduct." + Describe(result)
            : "Revit did not write the job file." + Describe(result);
    }

    /// <summary>Default MAJ path next to the model (or in Documents).</summary>
    public static string DefaultMajPath(Document doc)
    {
        string folder = string.IsNullOrEmpty(doc.PathName) || !Directory.Exists(Path.GetDirectoryName(doc.PathName))
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(doc.PathName)!;
        string name = string.IsNullOrEmpty(doc.Title) ? "Fabrication job" : Path.GetFileNameWithoutExtension(doc.Title);
        return Path.Combine(folder, name + ".MAJ");
    }

    private static string Describe(object? result)
    {
        if (result is null) return "";
        string text = result.ToString() ?? "";
        return text.Length == 0 || text == result.GetType().FullName ? "" : $"\n\nRevit: {text}";
    }

    private static int SafeCount(Func<int> get)
    {
        try { return get(); }
        catch { return 0; }
    }
}
