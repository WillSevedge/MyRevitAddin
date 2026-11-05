using Nexus.Agent;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>
/// Plant 3D objects in the drawing (pipes, fittings, valves, equipment, instruments, supports; P&amp;ID
/// symbols and lines) with their project data: tag, line number, size, spec, service, descriptions and
/// every other property Plant 3D shows in the Properties palette. Values are stored in the project
/// database, so they are edited through Plant 3D (see <see cref="PlantPropertyWriter"/>).
/// </summary>
internal sealed class PlantObjectsReader : IHostDataReader<Document>
{
    public const string IdPrefix = "Plant:";

    internal static readonly string[] ConnectionClasses = { "Gasket", "BoltSet", "Buttweld", "Weld", "Connector", "Fastener" };
    internal static readonly string[] NameProperties = { "Tag", "LineNumberTag", "PartFamilyLongDesc", "Description", "ShortDescription" };

    private readonly PlantApi _api;
    private readonly ObjectPropertyReader _objects;

    public PlantObjectsReader(PlantApi api, ObjectPropertyReader objects)
    {
        _api = api;
        _objects = objects;
    }

    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "plant.objects",
        DisplayName = "Plant 3D Objects",
        Domain = "Plant 3D",
        Description = "Pipes, fittings, valves, equipment, instruments, supports (and P&ID symbols and lines) with their Plant 3D project data: " +
                      "tag, line number, size, spec, service, descriptions... Values are edited through Plant 3D's project database.",
        Options =
        {
            ReaderOption.Bool("visibleOnly", "Only properties shown in the Properties palette", true),
            ReaderOption.Bool("connections", "Include gaskets, bolt sets, welds and other connections", false),
            ReaderOption.Bool("drawingProperties", "Include AutoCAD properties (layer, colour...)", false),
            ReaderOption.Int("maxObjects", "Max objects", 10000),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var project = _api.CurrentProject()
            ?? throw new AgentException(ErrorCodes.NotImplemented, "No Plant 3D project is open. Open the drawing from Plant 3D's Project Manager.");

        bool visibleOnly = ctx.GetBool("visibleOnly");
        bool connections = ctx.GetBool("connections");
        bool drawingProps = ctx.GetBool("drawingProperties");
        int max = ctx.GetInt("maxObjects", 10000);
        string? blocker = doc.IsReadOnly ? "Drawing is read-only" : null;
        var options = new ObjectReadOptions { Com = true, Managed = true, Attributes = false, TableCells = false };

        using var docLock = AcadDocumentProvider.LockForRead(doc);
        var db = doc.Database;
        int linked = 0;
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var link = _api.Link(project, id);
                if (link is null) continue;
                var (dlm, row) = link.Value;

                var props = _api.Properties(dlm, id, row, visibleOnly);
                string cls = _api.ClassName(dlm, id, row) ?? Value(props, "PnPClassName") ?? id.ObjectClass.DxfName;
                if (!connections && ConnectionClasses.Any(c => cls.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;

                if (++linked > max)
                {
                    ctx.Truncated = true;
                    ctx.Warn($"Only the first {max} objects were read (option 'Max objects').");
                    break;
                }

                string handle = id.Handle.ToString();
                string? tag = Value(props, "Tag");
                var item = new DataItem
                {
                    Id = handle,
                    ItemType = cls,
                    Name = NameProperties.Select(n => Value(props, n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? cls,
                    Key = string.IsNullOrWhiteSpace(tag) ? null : tag,
                };

                var group = new PropertyGroup("Plant 3D");
                foreach (var (name, value) in props)
                {
                    bool system = name.StartsWith("PnP", StringComparison.Ordinal);
                    group.Properties.Add(new PropertyValue
                    {
                        Name = name,
                        Id = IdPrefix + name,
                        Source = PropertySource.Managed,
                        StorageType = "String",
                        Value = value,
                        RawValue = value,
                        IsReadOnly = blocker is not null || system,
                        ReadOnlyReason = blocker ?? (system ? "Kept by Plant 3D's project database." : null),
                        OwnerId = handle,
                    });
                }
                item.Groups.Add(group);

                if (drawingProps)
                {
                    try
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
                        item.Groups.AddRange(_objects.Read(obj, tr, options, blocker).Groups);
                    }
                    catch (System.Exception ex)
                    {
                        ctx.Warn($"{cls} {handle}: AutoCAD properties not read ({ex.Message}).");
                    }
                }
                ctx.Items.Add(item);
            }
            tr.Commit();
        }
        finally
        {
            _objects.Journal.Flush();
        }

        if (linked == 0)
            ctx.Warn("No Plant 3D objects in this drawing's model space, or the drawing is not part of the open Plant 3D project.");
    }

    internal static string? Value(List<KeyValuePair<string, string>> props, string name) =>
        props.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
