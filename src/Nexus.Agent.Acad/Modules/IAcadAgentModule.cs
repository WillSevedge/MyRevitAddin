using Nexus.Agent;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Modules;

/// <summary>
/// An optional, vertical-specific module (Civil 3D, Map 3D, ...). It lives in its
/// own assembly so the core agent never references the vertical's API. The core
/// loads it only when modules.json's detection rules match the running product.
/// </summary>
public interface IAcadAgentModule
{
    string Name { get; }

    /// <summary>Called once on AutoCAD's main thread after the product finished starting.</summary>
    void Initialize(AcadModuleContext context);
}

/// <summary>
/// Writes back properties that a module reads itself (e.g. Plant 3D project data). Called on AutoCAD's
/// main thread with the document locked, after the core's own transaction has closed.
/// </summary>
public interface IAcadPropertyWriter
{
    /// <summary>True for the changes this writer owns (usually by a property id prefix).</summary>
    bool Handles(PropertyChange change);

    /// <summary>Sets the value and fills in <paramref name="result"/> (Status, Message, NewValue).</summary>
    void Apply(Document doc, ObjectId id, PropertyChange change, ChangeResult result);
}

public sealed class AcadModuleContext
{
    internal AcadModuleContext(AgentLog log, ReaderRegistry<Document> readers, ObjectPropertyReader objects, List<IAcadPropertyWriter> writers,
        ToolRegistry<Document> tools)
    {
        Log = log;
        Readers = readers;
        Objects = objects;
        Writers = writers;
        Tools = tools;
    }

    public AgentLog Log { get; }
    public ReaderRegistry<Document> Readers { get; }
    public ObjectPropertyReader Objects { get; }
    /// <summary>Add a writer for the properties the module's readers return.</summary>
    public List<IAcadPropertyWriter> Writers { get; }
    /// <summary>Add tools for AI assistants (Nexus.Mcp offers them as autocad_&lt;name&gt;).</summary>
    public ToolRegistry<Document> Tools { get; }
}
