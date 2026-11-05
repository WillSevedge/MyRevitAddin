using Nexus.Agent;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

internal static class RevitReaders
{
    public static ReaderRegistry<Document> CreateRegistry()
    {
        var registry = new ReaderRegistry<Document>();
        registry.Register(new SheetsReader());
        registry.Register(new ProjectInfoReader());
        registry.Register(new RevisionsReader());
        registry.Register(new FabricationPartsReader());
        registry.Register(new FabricationDatabaseReader());

        // TODO: future readers. Registered so the hub lists them; they return NotImplemented.
        registry.Register(new PlaceholderReader<Document>("revit.elements", "Model Elements by Category", "Elements",
            "TODO: model elements filtered by category with instance/type parameters."));
        registry.Register(new PlaceholderReader<Document>("revit.schedules", "Schedules", "Schedules",
            "TODO: schedule definitions and cell contents."));
        registry.Register(new PlaceholderReader<Document>("revit.views", "Views", "Views",
            "TODO: views and view templates with their parameters."));
        registry.Register(new PlaceholderReader<Document>("revit.rooms", "Rooms and Spaces", "Spaces",
            "TODO: rooms and MEP spaces with parameters."));
        registry.Register(new PlaceholderReader<Document>("revit.mepsystems", "MEP Systems", "MEP",
            "TODO: mechanical, piping and electrical systems."));
        return registry;
    }
}
