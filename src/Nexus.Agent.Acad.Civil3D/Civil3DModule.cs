using Nexus.Agent.Acad.Modules;

namespace Nexus.Agent.Acad.Civil3D;

/// <summary>Registers the Civil 3D readers and property extenders.</summary>
public sealed class Civil3DModule : IAcadAgentModule
{
    public string Name => "Civil3D";

    public void Initialize(AcadModuleContext context)
    {
        context.Readers.Register(new CivilObjectsReader(context.Objects));
        context.Objects.Extenders.Add(new CivilMethodPropertiesExtender(context.Objects));
        CivilTools.Register(context.Tools);

        // Palette-style fallback categories for common Civil 3D properties.
        foreach (var name in new[] { "StyleName", "StyleId", "LabelStyleName", "ProfileStyleName", "SurfaceStyleName" })
            context.Objects.Categories.Add(name, "Information");
        foreach (var name in new[] { "StartingStation", "EndingStation", "Length", "StationIndexIncrement", "ReferencePointStation" })
            context.Objects.Categories.Add(name, "Station");
    }
}
