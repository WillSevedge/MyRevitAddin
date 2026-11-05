using System.Diagnostics;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var app = AgentApplication.Instance;
            var status = app?.Server?.Status;
            var dialog = new TaskDialog("Nexus Agent")
            {
                MainInstruction = status is null ? "The Nexus agent is not running." : $"Agent is {status.State}",
                MainContent = status is null
                    ? "See the log for details."
                    : $"Pipe: {status.PipeName}\n" +
                      $"Hub connections: {status.Clients}\n" +
                      $"Requests handled: {status.RequestsHandled}\n" +
                      $"Host: {app?.Host?.Name}\n" +
                      (status.LastError is null ? "" : $"Last error: {status.LastError}\n"),
                FooterText = "Log: " + (app?.Log.FilePath ?? NexusPaths.LogsDir),
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show the Nexus hub",
                "Nexus runs in the background (tray icon next to the clock).");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open log folder");
            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    var error = HubControl.Show(app?.Log ?? new AgentLog("revit"));
                    if (error is not null) TaskDialog.Show("Nexus", error);
                    break;
                case TaskDialogResult.CommandLink2:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NexusPaths.LogsDir}\"") { UseShellExecute = true });
                    break;
            }
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>Keeps the ribbon button enabled with no document open (e.g. on the Home screen).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
