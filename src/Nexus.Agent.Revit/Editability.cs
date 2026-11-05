using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

internal static class Editability
{
    /// <summary>
    /// Returns why the element cannot be edited right now, or null if it can.
    /// Covers read-only/linked documents and worksharing ownership. <paramref name="askCentral"/> also asks the
    /// central model whether the element changed there; for cloud models (Autodesk Docs / ACC) that is a round trip
    /// to the server per element, so reads (hundreds of elements) leave it out and only writes ask.
    /// </summary>
    public static string? Blocker(Document doc, Element element, bool askCentral = false)
    {
        if (doc.IsLinked) return "Linked document (read-only)";
        // IsReadOnlyFile is the file state; Document.IsReadOnly is a passing state (e.g. while Revit
        // processes warnings) and must not lock values when they are read.
        if (doc.IsReadOnlyFile) return "The file is open read-only";
        if (!doc.IsWorkshared) return null;

        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, element.Id, out string owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                return $"Borrowed by {owner}";

            if (askCentral)
            {
                var updates = WorksharingUtils.GetModelUpdatesStatus(doc, element.Id);
                if (updates == ModelUpdatesStatus.UpdatedInCentral)
                    return "Changed in central: Reload Latest before editing";
                if (updates == ModelUpdatesStatus.DeletedInCentral)
                    return "Deleted in central";
            }

            var workset = doc.GetWorksetTable().GetWorkset(element.WorksetId);
            if (workset is not null && !workset.IsEditable && !string.IsNullOrEmpty(workset.Owner)
                && !string.Equals(workset.Owner, doc.Application.Username, StringComparison.OrdinalIgnoreCase))
                return $"Workset '{workset.Name}' is owned by {workset.Owner}";
        }
        catch (Exception ex)
        {
            return "Could not check worksharing status: " + ex.Message;
        }
        return null;
    }

    public static string WorksetName(Document doc, Element element)
    {
        if (!doc.IsWorkshared) return "";
        try { return doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name ?? ""; }
        catch { return ""; }
    }
}
