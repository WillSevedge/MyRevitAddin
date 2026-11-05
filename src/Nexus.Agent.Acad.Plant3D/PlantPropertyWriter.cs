using Nexus.Agent.Acad.Modules;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>Writes Plant 3D properties (tag, line number, service, descriptions...) through the project's DataLinksManager.</summary>
internal sealed class PlantPropertyWriter : IAcadPropertyWriter
{
    private readonly PlantApi _api;

    public PlantPropertyWriter(PlantApi api) => _api = api;

    public bool Handles(PropertyChange change) =>
        change.PropertyId?.StartsWith(PlantObjectsReader.IdPrefix, StringComparison.Ordinal) == true;

    public void Apply(Document doc, ObjectId id, PropertyChange change, ChangeResult result)
    {
        var project = _api.CurrentProject();
        if (project is null)
        {
            result.Message = "No Plant 3D project is open.";
            return;
        }
        var link = _api.Link(project, id);
        if (link is null)
        {
            result.Message = "This object is not linked to the Plant 3D project any more.";
            return;
        }
        var (dlm, row) = link.Value;
        string name = change.PropertyId![PlantObjectsReader.IdPrefix.Length..];

        string before = PlantObjectsReader.Value(_api.Properties(dlm, id, row, false), name) ?? "";
        if (change.ExpectedRawValue is not null && !string.Equals(before, change.ExpectedRawValue, StringComparison.Ordinal))
        {
            result.Message = $"Changed in Plant 3D since it was read (now '{before}'). Run the reader again.";
            return;
        }

        _api.SetProperties(dlm, id, row, new[] { name }, new[] { change.Value ?? "" });

        string after = PlantObjectsReader.Value(_api.Properties(dlm, id, row, false), name) ?? "";
        result.NewValue = after;
        if (string.Equals(after, before, StringComparison.Ordinal))
        {
            result.Status = ChangeStatus.Unchanged;
            if (!string.Equals(after, change.Value ?? "", StringComparison.Ordinal))
                result.Message = "Plant 3D kept the old value (the property is calculated or read-only, e.g. a tag built from its parts).";
        }
        else
        {
            result.Status = ChangeStatus.Applied;
        }
    }
}
