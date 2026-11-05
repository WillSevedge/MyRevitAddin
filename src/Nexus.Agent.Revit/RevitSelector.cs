using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

/// <summary>
/// "Show in Revit": one sheet or view opens it; elements are selected and zoomed to.
/// Brings the document forward first if another one is active.
/// </summary>
internal sealed class RevitSelector : IHostSelector<Document>
{
    private readonly RevitDispatcher _dispatcher;

    public RevitSelector(RevitDispatcher dispatcher) => _dispatcher = dispatcher;

    public SelectResult Select(Document doc, IReadOnlyList<string> itemIds, AgentLog log)
    {
        var app = _dispatcher.Current ?? throw new InvalidOperationException("Not on the Revit API thread.");
        var elements = itemIds.Select(id => doc.GetElement(id)).Where(e => e is not null).ToList();
        if (elements.Count == 0)
            return new SelectResult { Message = "Nothing to show: the items no longer exist in the model." };

        var uidoc = Activate(app, doc);
        if (uidoc is null)
            return new SelectResult { Message = $"Switch to '{doc.Title}' in Revit (it is open but not active), then try again." };

        // A single view or sheet: open it.
        if (elements.Count == 1 && elements[0] is View view && !view.IsTemplate)
        {
            uidoc.ActiveView = view;
            return new SelectResult { Shown = 1, Message = $"Opened {Describe(view)}." };
        }

        var ids = elements.Select(e => e!.Id).ToList();
        uidoc.Selection.SetElementIds(ids);
        try
        {
            uidoc.ShowElements(ids);
        }
        catch (Exception ex)
        {
            log.Warn("ShowElements failed; the elements are selected but not zoomed to.", ex);
        }
        return new SelectResult { Shown = ids.Count, Message = $"Selected {ids.Count} element(s) in '{doc.Title}'." };
    }

    private static UIDocument? Activate(UIApplication app, Document doc)
    {
        var active = app.ActiveUIDocument;
        if (active is not null && active.Document.Equals(doc)) return active;
        if (doc.IsLinked || string.IsNullOrEmpty(doc.PathName)) return null;
        try
        {
            // Activates the already-open document (does not reopen it).
            return app.OpenAndActivateDocument(doc.PathName);
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(View view) =>
        view is ViewSheet sheet ? $"sheet {sheet.SheetNumber} - {sheet.Name}" : $"view '{view.Name}'";
}
