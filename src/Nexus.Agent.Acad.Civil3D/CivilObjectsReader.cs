using Nexus.Agent;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Nexus.Agent.Acad.Civil3D;

/// <summary>
/// Civil 3D model objects (alignments with their profiles, surfaces, pipe networks
/// with pipes and structures, corridors), each with all of its properties.
/// Civil 3D labels and tables in paper space are covered by acad.layouts.
/// </summary>
internal sealed class CivilObjectsReader : IHostDataReader<Document>
{
    private readonly ObjectPropertyReader _objects;

    public CivilObjectsReader(ObjectPropertyReader objects) => _objects = objects;

    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "civil3d.objects",
        DisplayName = "Civil 3D Objects",
        Domain = "Civil 3D",
        Description = "Alignments (+ profiles), surfaces, pipe networks (+ pipes, structures) and corridors with all properties.",
        Options =
        {
            ReaderOption.Bool("alignments", "Alignments", true),
            ReaderOption.Bool("profiles", "Profiles under each alignment", true),
            ReaderOption.Bool("surfaces", "Surfaces", true),
            ReaderOption.Bool("pipeNetworks", "Pipe networks", true),
            ReaderOption.Bool("pipesAndStructures", "Pipes and structures under each network", true),
            ReaderOption.Bool("corridors", "Corridors", true),
            ReaderOption.Bool("comProperties", "COM/ActiveX properties", true),
            ReaderOption.Bool("managedProperties", ".NET API properties", true),
            ReaderOption.Int("maxChildren", "Max children per object", 2000),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var options = new ObjectReadOptions
        {
            Com = ctx.GetBool("comProperties"),
            Managed = ctx.GetBool("managedProperties"),
            Attributes = false,
            TableCells = false,
        };
        string? blocker = doc.IsReadOnly ? "Drawing is read-only" : null;
        int maxChildren = ctx.GetInt("maxChildren", 2000);

        using var docLock = AcadDocumentProvider.LockForRead(doc);
        var db = doc.Database;
        try
        {
            var civil = CivilDocument.GetCivilDocument(db)
                        ?? throw new AgentException(ErrorCodes.NotImplemented, "This drawing has no Civil 3D data.");
            using var tr = db.TransactionManager.StartOpenCloseTransaction();

            if (ctx.GetBool("alignments"))
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    var item = ReadOne(id, tr, options, blocker, ctx);
                    if (item is null) continue;
                    if (ctx.GetBool("profiles") && tr.GetObject(id, OpenMode.ForRead) is Alignment al)
                        AddChildren(item, al.GetProfileIds(), tr, options, blocker, ctx, maxChildren);
                    ctx.Items.Add(item);
                }
            }

            if (ctx.GetBool("surfaces"))
                foreach (ObjectId id in civil.GetSurfaceIds())
                    AddIfRead(ReadOne(id, tr, options, blocker, ctx), ctx);

            if (ctx.GetBool("pipeNetworks"))
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    var item = ReadOne(id, tr, options, blocker, ctx);
                    if (item is null) continue;
                    if (ctx.GetBool("pipesAndStructures") && tr.GetObject(id, OpenMode.ForRead) is Network net)
                    {
                        AddChildren(item, net.GetPipeIds(), tr, options, blocker, ctx, maxChildren);
                        AddChildren(item, net.GetStructureIds(), tr, options, blocker, ctx, maxChildren);
                    }
                    ctx.Items.Add(item);
                }
            }

            if (ctx.GetBool("corridors"))
                foreach (ObjectId id in civil.CorridorCollection)
                    AddIfRead(ReadOne(id, tr, options, blocker, ctx), ctx);

            // TODO: pressure networks, feature lines, sample line groups, parcels/sites, point groups.
            tr.Commit();
        }
        finally
        {
            _objects.Journal.Flush();
        }
    }

    private static void AddIfRead(DataItem? item, ReadContext ctx)
    {
        if (item is not null) ctx.Items.Add(item);
    }

    private DataItem? ReadOne(ObjectId id, Transaction tr, ObjectReadOptions options, string? blocker, ReadContext ctx)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        try
        {
            var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
            var item = _objects.Read(obj, tr, options, blocker);
            item.Key ??= ValueFormatter.TryName(obj);
            return item;
        }
        catch (Exception ex)
        {
            ctx.Warn($"Civil object {id.Handle}: {ex.Message}");
            return null;
        }
    }

    private void AddChildren(DataItem parent, ObjectIdCollection ids, Transaction tr, ObjectReadOptions options,
        string? blocker, ReadContext ctx, int max)
    {
        int n = 0;
        foreach (ObjectId id in ids)
        {
            if (++n > max)
            {
                ctx.Truncated = true;
                ctx.Warn($"{parent.Name}: stopped after {max} children.");
                return;
            }
            var child = ReadOne(id, tr, options, blocker, ctx);
            if (child is not null) parent.Children.Add(child);
        }
    }
}
