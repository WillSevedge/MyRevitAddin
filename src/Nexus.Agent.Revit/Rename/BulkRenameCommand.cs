using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit.Rename;

/// <summary>Nexus tab › Bulk Rename.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class BulkRenameCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc is null)
        {
            message = "Open a project first.";
            return Result.Failed;
        }
        var log = AgentApplication.Instance?.Log ?? new Nexus.Agent.AgentLog("revit");
        try
        {
            new BulkRenameWindow(uidoc, log).ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            log.Error("Bulk Rename could not open.", ex);
            message = "Bulk Rename could not open: " + ex.Message;
            return Result.Failed;
        }
    }
}
