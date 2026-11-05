using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

/// <summary>
/// Every revision in the project (Sheet Issues/Revisions), in sequence order: all of its settings
/// (date, description, issued, issued to/by, visibility, numbering...) and the sheets it is on.
/// Settings are edited like any parameter; Revit locks them once a revision is issued.
/// </summary>
internal sealed class RevisionsReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.revisions",
        DisplayName = "Revisions",
        Domain = "Revisions",
        Description = "Every revision (Sheet Issues/Revisions): sequence, number, date, description, issued, issued to/by, visibility, numbering, and the sheets it is on.",
        Options =
        {
            ReaderOption.Bool("hiddenParameters", "Include parameters not shown in Properties", false),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var revisions = RevisionsOnSheets.ProjectRevisions(doc);
        if (revisions.Count == 0)
        {
            ctx.Warn("This model has no revisions.");
            return;
        }

        // Which sheets show each revision (and on which the number differs, with per-sheet numbering).
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
        var onSheets = revisions.ToDictionary(r => r.Id, _ => new List<(ViewSheet Sheet, bool FromClouds)>());
        foreach (var sheet in sheets)
        {
            var added = sheet.GetAdditionalRevisionIds().ToHashSet();
            foreach (var id in sheet.GetAllRevisionIds())
                if (onSheets.TryGetValue(id, out var list)) list.Add((sheet, !added.Contains(id)));
        }

        foreach (var r in revisions)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            string? blocker = Editability.Blocker(doc, r);
            var item = new DataItem
            {
                Id = r.UniqueId,
                ItemType = "Revision",
                Name = RevisionsOnSheets.Label(r),
                Key = r.SequenceNumber.ToString(CultureInfo.InvariantCulture),
            };

            var summary = new PropertyGroup("Revision");
            summary.Properties.Add(ElementInfo.Derived("Sequence", r.SequenceNumber.ToString(CultureInfo.InvariantCulture)));
            summary.Properties.Add(ElementInfo.Derived("Revision Number", r.RevisionNumber));
            summary.Properties.Add(ElementInfo.Derived("Issued", r.Issued ? "Yes" : "No"));
            try
            {
                if (doc.GetElement(r.RevisionNumberingSequenceId) is RevisionNumberingSequence seq)
                    summary.Properties.Add(ElementInfo.Derived("Numbering Sequence", seq.Name));
            }
            catch { /* not available */ }
            var list = onSheets[r.Id];
            summary.Properties.Add(ElementInfo.Derived("Sheet Count", list.Count.ToString(CultureInfo.InvariantCulture)));
            summary.Properties.Add(ElementInfo.Derived("Sheets", string.Join(", ", list.Select(x => x.Sheet.SheetNumber))));
            summary.Properties.Add(ElementInfo.Derived("Sheets With Clouds", string.Join(", ", list.Where(x => x.FromClouds).Select(x => x.Sheet.SheetNumber))));
            item.Groups.Add(summary);

            item.Groups.Add(ElementInfo.Group(doc, r, "Element", blocker));
            item.Groups.AddRange(ParameterReader.ReadGroups(doc, r, "", blocker, ctx.GetBool("hiddenParameters")));
            ctx.Items.Add(item);
        }
    }
}
