using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>
/// Which revisions are shown on a sheet, one Yes/No property per project revision, like Revit's
/// "Revisions on Sheet" dialog. Revisions added by hand are editable; a revision that is on the
/// sheet because of a revision cloud (or tag) on it cannot be removed, the same as in Revit.
/// </summary>
internal static class RevisionsOnSheets
{
    public const string GroupName = "Revisions on Sheet";
    public const string IdPrefix = "RevOnSheet:";

    /// <summary>All project revisions in sequence order.</summary>
    public static List<Revision> ProjectRevisions(Document doc) =>
        Revision.GetAllRevisionIds(doc).Select(doc.GetElement).OfType<Revision>().ToList();

    /// <summary>"Seq. 3 - Revision 3": the same label Revit uses in the dialog.</summary>
    public static string Label(Revision r) => $"Seq. {r.SequenceNumber} - {r.Description}";

    public static PropertyGroup Group(ViewSheet sheet, IReadOnlyList<Revision> revisions, string? blocker,
        ICollection<ElementId>? allRevisionIds = null)
    {
        var g = new PropertyGroup(GroupName);
        var all = (allRevisionIds ?? sheet.GetAllRevisionIds()).ToHashSet();
        var added = sheet.GetAdditionalRevisionIds().ToHashSet();
        foreach (var r in revisions)
        {
            bool on = all.Contains(r.Id);
            bool fromClouds = on && !added.Contains(r.Id);
            string? reason = fromClouds ? "On this sheet because of revision clouds on it (remove the clouds to remove the revision)" : blocker;
            g.Properties.Add(new PropertyValue
            {
                Name = Label(r),
                Id = IdPrefix + r.UniqueId,
                OwnerId = sheet.UniqueId,
                Source = PropertySource.Assignment,
                StorageType = "Boolean",
                DataType = "Yes/No",
                Value = on ? "Yes" : "No",
                RawValue = on ? "Yes" : "No",
                IsReadOnly = reason is not null,
                ReadOnlyReason = reason,
            });
        }
        return g;
    }

    /// <summary>Current Yes/No for one revision on one sheet.</summary>
    public static string State(ViewSheet sheet, Revision r) => sheet.GetAllRevisionIds().Contains(r.Id) ? "Yes" : "No";

    /// <summary>
    /// Shows or hides the revision on the sheet (inside an open transaction). Returns null or why not;
    /// <paramref name="changed"/> says whether the sheet was modified.
    /// Note: GetAllRevisionIds only reflects the change after Revit regenerates, so do not use it
    /// to check the result inside the transaction.
    /// </summary>
    public static string? Set(ViewSheet sheet, Revision r, bool show, out bool changed)
    {
        changed = false;
        var added = sheet.GetAdditionalRevisionIds().ToList();
        bool onSheet = sheet.GetAllRevisionIds().Contains(r.Id);
        if (show)
        {
            if (onSheet || added.Contains(r.Id)) return null;
            added.Add(r.Id);
        }
        else
        {
            if (!onSheet) return null;
            if (!added.Remove(r.Id))
                return "This revision is on the sheet because of revision clouds on it; remove the clouds to remove the revision.";
        }
        sheet.SetAdditionalRevisionIds(added);
        changed = true;
        return null;
    }
}
