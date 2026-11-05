using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

/// <summary>
/// Every ViewSheet: all sheet parameters (built-in, project, shared), title block
/// instance and type parameters, and the current revision.
/// Speed: this runs on Revit's main thread, so the work that repeats per sheet is done once per read
/// (title blocks found with one search, each title block type read once, parameter labels cached).
/// </summary>
internal sealed class SheetsReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.sheets",
        DisplayName = "Sheets",
        Domain = "Sheets",
        Description = "All sheets with every sheet parameter, title block instance/type parameters and current revision.",
        Options =
        {
            ReaderOption.Bool("titleBlockInstance", "Title block instance parameters", true),
            ReaderOption.Bool("titleBlockType", "Title block type parameters", true),
            ReaderOption.Bool("revisions", "Current revision and revisions on each sheet", true),
            // Off by default: roughly doubles what is read and sent, and the Properties palette ones are what people edit.
            ReaderOption.Bool("hiddenParameters", "Include parameters not shown in Properties", false),
            ReaderOption.Bool("placeholders", "Include placeholder sheets", true),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        bool tbInstance = ctx.GetBool("titleBlockInstance");
        bool tbType = ctx.GetBool("titleBlockType");
        bool revisions = ctx.GetBool("revisions");
        bool hidden = ctx.GetBool("hiddenParameters");
        bool placeholders = ctx.GetBool("placeholders");

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => placeholders || !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectRevisions = revisions ? RevisionsOnSheets.ProjectRevisions(doc) : new List<Revision>();
        var cache = new ParameterCache();
        // One search for every title block in the model instead of one per sheet (a view-filtered search per
        // sheet is slow on large models).
        var titleBlocksBySheet = tbInstance || tbType ? TitleBlocksBySheet(doc) : new Dictionary<long, List<Element>>();

        foreach (var sheet in sheets)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                ctx.Items.Add(ReadSheet(doc, sheet, ctx, tbInstance, tbType, revisions, hidden, projectRevisions, cache, titleBlocksBySheet));
            }
            catch (Exception ex)
            {
                ctx.Warn($"Sheet {SafeNumber(sheet)}: {ex.Message}");
                ctx.Log.Warn($"Sheet {SafeNumber(sheet)} failed.", ex);
            }
        }
    }

    /// <summary>Title block instances by the sheet they are on, in element id order (as the per-sheet search returned them).</summary>
    private static Dictionary<long, List<Element>> TitleBlocksBySheet(Document doc)
    {
        var bySheet = new Dictionary<long, List<Element>>();
        foreach (var tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
        {
            long owner = tb.OwnerViewId.Value;
            if (!bySheet.TryGetValue(owner, out var list)) bySheet[owner] = list = new List<Element>();
            list.Add(tb);
        }
        foreach (var list in bySheet.Values) list.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return bySheet;
    }

    private static DataItem ReadSheet(Document doc, ViewSheet sheet, ReadContext ctx,
        bool tbInstance, bool tbType, bool revisions, bool hidden, IReadOnlyList<Revision> projectRevisions,
        ParameterCache cache, Dictionary<long, List<Element>> titleBlocksBySheet)
    {
        string? blocker = Editability.Blocker(doc, sheet);
        var item = new DataItem
        {
            Id = sheet.UniqueId,
            ItemType = sheet.IsPlaceholder ? "Placeholder Sheet" : "Sheet",
            Name = $"{sheet.SheetNumber} - {sheet.Name}",
            Key = sheet.SheetNumber,
        };

        var element = ElementInfo.Group(doc, sheet, "Element", blocker);
        element.Properties.Add(ElementInfo.Derived("Placeholder", sheet.IsPlaceholder ? "Yes" : "No"));
        item.Groups.Add(element);

        var sheetGroups = ParameterReader.ReadGroups(doc, sheet, "Sheet · ", blocker, hidden, cache);
        // Asked once per sheet: Revit works the list out from the revision clouds on the sheet and its views.
        var revisionIds = revisions ? sheet.GetAllRevisionIds() : null;
        if (revisionIds is not null && !sheet.IsPlaceholder) AddRevisionsOnSheetSummary(doc, revisionIds, sheetGroups);
        item.Groups.AddRange(sheetGroups);

        if (revisionIds is not null)
        {
            item.Groups.Add(ReadRevisions(doc, sheet, revisionIds));
            if (!sheet.IsPlaceholder && projectRevisions.Count > 0)
                item.Groups.Add(RevisionsOnSheets.Group(sheet, projectRevisions, blocker, revisionIds));
        }

        if (tbInstance || tbType)
        {
            var titleBlocks = titleBlocksBySheet.TryGetValue(sheet.Id.Value, out var found) ? found : new List<Element>();
            ReadTitleBlocks(doc, sheet, titleBlocks, item, tbInstance, tbType, hidden, ctx, cache);
        }

        return item;
    }

    /// <summary>
    /// "Revisions on Sheet" where the Properties palette has its Edit... button (after Appears In Sheet
    /// List): the revisions shown on the sheet. Change them with right-click › Revisions on sheet in the hub.
    /// </summary>
    private static void AddRevisionsOnSheetSummary(Document doc, IList<ElementId> revisionIds, List<PropertyGroup> groups)
    {
        var shown = revisionIds.Select(id => doc.GetElement(id)).OfType<Revision>()
            .OrderBy(r => r.SequenceNumber).Select(RevisionsOnSheets.Label).ToList();
        var value = ElementInfo.Derived(RevisionsOnSheets.GroupName, string.Join(", ", shown));
        value.ReadOnlyReason = "Change with right-click › Revisions on sheet (like Revit's Edit... button)";

        var group = groups.FirstOrDefault(g => g.Properties.Any(p => p.Id == nameof(BuiltInParameter.SHEET_SCHEDULED)))
                    ?? groups.FirstOrDefault(g => g.Properties.Any(p => p.Id == nameof(BuiltInParameter.SHEET_NUMBER)));
        if (group is null) return;
        int after = group.Properties.FindIndex(p => p.Id == nameof(BuiltInParameter.SHEET_SCHEDULED));
        group.Properties.Insert(after >= 0 ? after + 1 : group.Properties.Count, value);
    }

    private static void ReadTitleBlocks(Document doc, ViewSheet sheet, IList<Element> titleBlocks, DataItem item,
        bool instance, bool type, bool hidden, ReadContext ctx, ParameterCache cache)
    {
        var summary = new PropertyGroup("Title Block");
        summary.Properties.Add(ElementInfo.Derived("Title Block Count", titleBlocks.Count.ToString(CultureInfo.InvariantCulture)));
        item.Groups.Add(summary);

        if (titleBlocks.Count > 1)
            ctx.Warn($"Sheet {sheet.SheetNumber} has {titleBlocks.Count} title blocks; each is listed separately.");

        for (int i = 0; i < titleBlocks.Count; i++)
        {
            var tb = titleBlocks[i];
            string label = titleBlocks.Count == 1 ? "Title Block" : $"Title Block {i + 1}";
            var tbType = doc.GetElement(tb.GetTypeId()) as ElementType;

            summary.Properties.Add(ElementInfo.Derived($"{label} Family", tbType?.FamilyName ?? ""));
            summary.Properties.Add(ElementInfo.Derived($"{label} Type", tbType?.Name ?? ""));

            if (instance)
                item.Groups.AddRange(ParameterReader.ReadGroups(doc, tb, $"{label} (Instance) · ",
                    Editability.Blocker(doc, tb), hidden, cache));

            // The same type is on most sheets: read it once per read.
            if (type && tbType is not null)
            {
                string prefix = $"{label} (Type) · ";
                item.Groups.AddRange(cache.TypeGroups(tbType.Id, prefix, () =>
                    ParameterReader.ReadGroups(doc, tbType, prefix, Editability.Blocker(doc, tbType), hidden, cache)));
            }
        }
    }

    private static PropertyGroup ReadRevisions(Document doc, ViewSheet sheet, IList<ElementId> all)
    {
        var g = new PropertyGroup("Current Revision");
        var currentId = sheet.GetCurrentRevision();
        var current = currentId == ElementId.InvalidElementId ? null : doc.GetElement(currentId) as Revision;

        if (current is null)
        {
            g.Properties.Add(ElementInfo.Derived("Revision Number", ""));
            g.Properties.Add(ElementInfo.Derived("Revision Date", ""));
            g.Properties.Add(ElementInfo.Derived("Revision Description", ""));
        }
        else
        {
            string onSheet;
            try { onSheet = sheet.GetRevisionNumberOnSheet(currentId); }
            catch { onSheet = current.RevisionNumber; }

            g.Properties.Add(ElementInfo.Derived("Revision Number", onSheet));
            g.Properties.Add(ElementInfo.Derived("Revision Date", current.RevisionDate));
            g.Properties.Add(ElementInfo.Derived("Revision Description", current.Description));
            g.Properties.Add(ElementInfo.Derived("Revision Sequence", current.SequenceNumber.ToString(CultureInfo.InvariantCulture)));
            g.Properties.Add(ElementInfo.Derived("Revision Issued", current.Issued ? "Yes" : "No"));
            g.Properties.Add(ElementInfo.Derived("Revision Issued By", current.IssuedBy));
            g.Properties.Add(ElementInfo.Derived("Revision Issued To", current.IssuedTo));
        }

        g.Properties.Add(ElementInfo.Derived("Revisions On Sheet", all.Count.ToString(CultureInfo.InvariantCulture)));
        var lines = new List<string>();
        foreach (var id in all)
        {
            if (doc.GetElement(id) is not Revision r) continue;
            string num;
            try { num = sheet.GetRevisionNumberOnSheet(id); } catch { num = r.RevisionNumber; }
            lines.Add($"{num} | {r.RevisionDate} | {r.Description}");
        }
        g.Properties.Add(ElementInfo.Derived("Revision History", string.Join("; ", lines)));
        return g;
    }

    private static string SafeNumber(ViewSheet s)
    {
        try { return s.SheetNumber; } catch { return s.Id.Value.ToString(CultureInfo.InvariantCulture); }
    }
}
