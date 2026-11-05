using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>
/// The open Plant 3D project: its settings, its parts (Piping, P&amp;ID, Ortho, Iso) and every drawing
/// registered in each part. Read-only (change these in Plant 3D's Project Manager).
/// </summary>
internal sealed class PlantProjectReader : IHostDataReader<Document>
{
    private readonly PlantApi _api;

    public PlantProjectReader(PlantApi api) => _api = api;

    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "plant.project",
        DisplayName = "Plant 3D Project",
        Domain = "Plant 3D",
        Description = "The open Plant 3D project: settings, parts (Piping, P&ID, Ortho, Iso) and every drawing registered in the project.",
        Options =
        {
            ReaderOption.Bool("drawings", "Project drawings", true),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var project = _api.CurrentProject()
            ?? throw new AgentException(ErrorCodes.NotImplemented, "No Plant 3D project is open. Open one in Plant 3D's Project Manager.");

        string projectName = PlantApi.ScalarText(project, "ProjectName") ?? PlantApi.ScalarText(project, "Name") ?? "Plant 3D project";
        ctx.Items.Add(Item("plant-project", "Project", projectName, projectName, "Project", PlantApi.Scalars(project)));

        foreach (var partName in PlantApi.PartNames)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var part = _api.Part(project, partName);
            if (part is null) continue;
            string label = partName == "PnId" ? "P&ID" : partName;
            ctx.Items.Add(Item($"plant-part-{partName}", "Project Part", label, label, "Project Part", PlantApi.Scalars(part)));

            if (!ctx.GetBool("drawings")) continue;
            int n = 0;
            foreach (var drawing in _api.Drawings(part))
            {
                var values = PlantApi.Scalars(drawing);
                string file = First(values, "AbsoluteFileName", "ResolvedFilePath", "RelativeFileName", "FileName") ?? $"Drawing {++n}";
                string name = Path.GetFileNameWithoutExtension(file);
                values.Insert(0, new("Project Part", label));
                ctx.Items.Add(Item($"plant-drawing-{partName}-{file}", label + " Drawing", name, name, "Drawing", values));
            }
        }
    }

    private static string? First(List<KeyValuePair<string, string>> values, params string[] names) =>
        names.Select(n => values.FirstOrDefault(v => v.Key == n).Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static DataItem Item(string id, string type, string name, string key, string group, List<KeyValuePair<string, string>> values)
    {
        var item = new DataItem { Id = id, ItemType = type, Name = name, Key = key };
        var g = new PropertyGroup(group);
        foreach (var (k, v) in values)
            g.Properties.Add(new PropertyValue
            {
                Name = k,
                Value = v,
                RawValue = v,
                Source = PropertySource.Managed,
                StorageType = "String",
                IsReadOnly = true,
                ReadOnlyReason = "Change project settings in Plant 3D's Project Manager.",
            });
        item.Groups.Add(g);
        return item;
    }
}
