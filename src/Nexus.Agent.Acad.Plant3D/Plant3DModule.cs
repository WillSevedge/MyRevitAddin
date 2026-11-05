using Nexus.Agent.Acad.Modules;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>Registers the Plant 3D readers (project data of piping, P&amp;ID, ortho and iso drawings) and their writer.</summary>
public sealed class Plant3DModule : IAcadAgentModule
{
    public string Name => "Plant3D";

    public void Initialize(AcadModuleContext context)
    {
        var api = new PlantApi(context.Log);
        context.Readers.Register(new PlantObjectsReader(api, context.Objects));
        context.Readers.Register(new PlantProjectReader(api));
        context.Writers.Add(new PlantPropertyWriter(api));
        PlantTools.Register(context.Tools, api);
    }
}
