using System.Windows;
using Nexus.Agent;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit.Fabrication;

internal static class FabricationCommands
{
    public static AgentLog Log => AgentApplication.Instance?.Log ?? new AgentLog("revit");

    /// <summary>Asks where to save, then writes the MAJ job. Shared by the ribbon button and the database window.</summary>
    public static void Export(UIDocument uidoc, AgentLog log, Window? owner)
    {
        try
        {
            var doc = uidoc.Document;
            var (ids, scope) = FabricationActions.PartsToExport(uidoc);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Export {scope} to a Fabrication job",
                Filter = "Fabrication job (*.MAJ)|*.MAJ",
                DefaultExt = ".MAJ",
                FileName = System.IO.Path.GetFileName(FabricationActions.DefaultMajPath(doc)),
                InitialDirectory = System.IO.Path.GetDirectoryName(FabricationActions.DefaultMajPath(doc)),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(owner) != true) return;

            string summary = FabricationActions.ExportMaj(doc, ids, dialog.FileName, holesForTaps: true);
            log.Info($"Exported {scope} to {dialog.FileName}");
            Show(owner, "Exported to Fabrication", summary, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            log.Warn("Exporting to a MAJ job failed.", ex);
            Show(owner, "Export failed", ex.Message, MessageBoxImage.Warning);
        }
    }

    private static void Show(Window? owner, string title, string text, MessageBoxImage icon)
    {
        if (owner is not null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
        else TaskDialog.Show(title, text);
    }
}

/// <summary>Nexus tab › Fabrication › Database: browse the fabrication configuration and run the bridge actions.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class FabricationDatabaseCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc is null || uidoc.Document.IsFamilyDocument)
            {
                TaskDialog.Show("Nexus", "Open a project to see its fabrication database.");
                return Result.Cancelled;
            }
            new FabricationWindow(uidoc, FabricationCommands.Log).ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>Nexus tab › Fabrication › Reload: pull database changes made in Fabrication CADmep into the model.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class FabricationReloadCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc is null) return Result.Cancelled;
        try
        {
            string summary = FabricationActions.Reload(uidoc.Document);
            FabricationCommands.Log.Info("Fabrication configuration reloaded. " + summary.Replace('\n', ' '));
            TaskDialog.Show("Fabrication configuration reloaded", summary);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            FabricationCommands.Log.Warn("Reloading the fabrication configuration failed.", ex);
            TaskDialog.Show("Reload failed", ex.Message);
            return Result.Cancelled;
        }
    }
}

/// <summary>Nexus tab › Fabrication › Export MAJ: selected parts (or the active view's) to a Fabrication job.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class FabricationExportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc is null) return Result.Cancelled;
        FabricationCommands.Export(uidoc, FabricationCommands.Log, null);
        return Result.Succeeded;
    }
}

/// <summary>Nexus tab › Fabrication › Parts in Nexus: opens the hub on the Fabrication parts view.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class FabricationPartsInHubCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var error = HubControl.Show(FabricationCommands.Log, "revit.fabrication.parts");
        if (error is not null) TaskDialog.Show("Nexus", error);
        return Result.Succeeded;
    }
}
