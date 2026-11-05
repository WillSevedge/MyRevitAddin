using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

/// <summary>Every Project Information parameter.</summary>
internal sealed class ProjectInfoReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.projectinfo",
        DisplayName = "Project Information",
        Domain = "Project",
        Description = "Every Project Information parameter (built-in, project and shared).",
        Options =
        {
            ReaderOption.Bool("hiddenParameters", "Include parameters not shown in Properties", true),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var info = doc.ProjectInformation;
        if (info is null)
        {
            ctx.Warn("This document has no Project Information (family documents do not).");
            return;
        }

        string? blocker = Editability.Blocker(doc, info);
        var item = new DataItem
        {
            Id = info.UniqueId,
            ItemType = "Project Information",
            Name = string.IsNullOrWhiteSpace(info.Name) ? doc.Title : info.Name,
            Key = info.Number,
        };
        item.Groups.Add(ElementInfo.Group(doc, info, "Element", blocker));
        item.Groups.AddRange(ParameterReader.ReadGroups(doc, info, "", blocker, ctx.GetBool("hiddenParameters")));
        ctx.Items.Add(item);
    }
}
