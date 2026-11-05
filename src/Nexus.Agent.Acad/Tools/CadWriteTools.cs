using Nexus.Agent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using static Nexus.Agent.Acad.Tools.CadModel;

namespace Nexus.Agent.Acad.Tools;

/// <summary>Draws entities: lines, polylines, rectangles, circles, arcs, points, text, mtext, blocks, dimensions.</summary>
internal sealed class CreateEntitiesTool : CadTool
{


    public CreateEntitiesTool() : base("create_entities") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        var items = args.Objects("items");
        if (items.Count == 0) throw new ToolException("Give at least one item to draw.");
        if (items.Count > 2000) throw new ToolException("At most 2000 items at a time.");
        bool createLayers = args.Bool("create_layers", true);
        return Write(doc, (db, tr) =>
        {
            var space = Space(db, tr, args.String("space"), OpenMode.ForWrite);
            var created = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var a = items[i];
                Entity e;
                try { e = Make(db, tr, a); }
                catch (ToolException ex) { throw new ToolException($"Item {i + 1}: {ex.Message} Nothing was drawn."); }
                e.SetDatabaseDefaults(db);
                Style(db, tr, e, a, createLayers);
                space.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
                if (e is BlockReference br) AddAttributes(tr, br, a);
                created.Add(new Dictionary<string, object?> { ["handle"] = Hex(e.ObjectId), ["type"] = TypeName(e), ["layer"] = e.Layer });
            }
            return new ToolOutput { Data = created, Summary = $"Drew {created.Count} entit{(created.Count == 1 ? "y" : "ies")}. One U in AutoCAD undoes it." };
        });
    }

    private static Entity Make(Database db, Transaction tr, ToolArgs a)
    {
        string type = a.RequiredString("type").ToLowerInvariant();
        double rot = Rad(a.Number("rotation") ?? 0);
        switch (type)
        {
            case "line":
                return new Line(Pt(a.RequiredPoint("start")), Pt(a.RequiredPoint("end")));
            case "polyline":
            case "rectangle":
            {
                List<double[]> pts;
                if (type == "rectangle")
                {
                    var c1 = a.RequiredPoint("corner1");
                    var c2 = a.RequiredPoint("corner2");
                    pts = new List<double[]> { c1, new[] { c2[0], c1[1] }, c2, new[] { c1[0], c2[1] } };
                }
                else pts = a.Points("points");
                if (pts.Count < 2) throw new ToolException("A polyline needs at least 2 points.");
                var bulges = a.Get("bulges") is { } ? a.Strings("bulges").Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList() : new List<double>();
                var pl = new Polyline();
                for (int i = 0; i < pts.Count; i++)
                    pl.AddVertexAt(i, new Point2d(pts[i][0], pts[i][1]), i < bulges.Count ? bulges[i] : 0, 0, 0);
                pl.Closed = type == "rectangle" || a.Bool("closed", false);
                if (a.Number("width") is { } w) pl.ConstantWidth = w;
                if (pts[0].Length > 2 && pts[0][2] != 0) pl.Elevation = pts[0][2];
                return pl;
            }
            case "circle":
                return new Circle(Pt(a.RequiredPoint("center")), Vector3d.ZAxis, Positive(a, "radius"));
            case "arc":
                return new Arc(Pt(a.RequiredPoint("center")), Positive(a, "radius"),
                    Rad(a.Number("start_angle") ?? throw new ToolException("'start_angle' is required.")),
                    Rad(a.Number("end_angle") ?? throw new ToolException("'end_angle' is required.")));
            case "point":
                return new DBPoint(Pt(a.RequiredPoint("position")));
            case "text":
                return new DBText
                {
                    Position = Pt(a.RequiredPoint("position")),
                    TextString = a.RequiredString("text"),
                    Height = a.Number("height") ?? db.Textsize,
                    Rotation = rot,
                    TextStyleId = db.Textstyle,
                };
            case "mtext":
                return new MText
                {
                    Location = Pt(a.RequiredPoint("position")),
                    Contents = a.RequiredString("text"),
                    TextHeight = a.Number("height") ?? db.Textsize,
                    Width = a.Number("mtext_width") ?? 0,
                    Rotation = rot,
                    TextStyleId = db.Textstyle,
                };
            case "block":
            {
                string name = a.RequiredString("name");
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(name)) throw new ToolException($"No block '{name}' in the drawing (see list_blocks).");
                double s = a.Number("scale") ?? 1;
                return new BlockReference(Pt(a.RequiredPoint("position")), bt[name]) { Rotation = rot, ScaleFactors = new Scale3d(s) };
            }
            case "dimension":
            {
                var p1 = Pt(a.RequiredPoint("start"));
                var p2 = Pt(a.RequiredPoint("end"));
                var line = a.Point("dimension_line") is { } dl ? Pt(dl) : DefaultDimensionLine(p1, p2, db);
                return new AlignedDimension(p1, p2, line, a.String("text") ?? "", db.Dimstyle);
            }
            default:
                throw new ToolException($"Cannot draw '{type}'. Types: line, polyline, rectangle, circle, arc, point, text, mtext, block, dimension.");
        }
    }

    private static double Positive(ToolArgs a, string name) =>
        a.Number(name) is { } v && v > 0 ? v : throw new ToolException($"'{name}' must be a positive number.");

    /// <summary>Offset the dimension line by a few text heights from the measured points.</summary>
    private static Point3d DefaultDimensionLine(Point3d p1, Point3d p2, Database db)
    {
        var dir = (p2 - p1);
        var normal = dir.Length > 0 ? dir.GetNormal().RotateBy(Math.PI / 2, Vector3d.ZAxis) : Vector3d.YAxis;
        var mid = p1 + dir * 0.5;
        return mid + normal * Math.Max(db.Textsize * 4, dir.Length * 0.1);
    }

    /// <summary>Adds the block's attributes (definition defaults, or the values given).</summary>
    private static void AddAttributes(Transaction tr, BlockReference br, ToolArgs a)
    {
        var values = a.Object("attributes");
        var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
        if (!btr.HasAttributeDefinitions) return;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
            var ar = new AttributeReference();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            string? given = values?.Root.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, ad.Tag, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: System.Text.Json.JsonValueKind.String } v
                ? v.GetString() : null;
            ar.TextString = given ?? ad.TextString;
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
        }
    }
}

/// <summary>Changes entities: layer, colour, linetype, move, rotate, scale, text, attributes.</summary>
internal sealed class ModifyEntitiesTool : CadTool
{
    public ModifyEntitiesTool() : base("modify_entities") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        var handles = args.Strings("handles");
        if (handles.Count == 0) throw new ToolException("Give one or more handles.");
        bool createLayers = args.Bool("create_layers", true);
        return Write(doc, (db, tr) =>
        {
            var entities = handles.Select(h => tr.GetObject(ById(db, h), OpenMode.ForWrite) as Entity
                                               ?? throw new ToolException($"'{h}' is not a drawing entity.")).ToList();
            Point3d basePoint;
            if (args.Point("base_point") is { } bp) basePoint = Pt(bp);
            else
            {
                var boxes = entities.Select(Box).Where(b => b is not null).ToList();
                basePoint = boxes.Count == 0 ? Point3d.Origin
                    : new Point3d((boxes.Min(b => b![0]) + boxes.Max(b => b![2])) / 2, (boxes.Min(b => b![1]) + boxes.Max(b => b![3])) / 2, 0);
            }
            var transforms = new List<Matrix3d>();
            if (args.Number("scale") is { } s)
            {
                if (s <= 0) throw new ToolException("'scale' must be positive.");
                transforms.Add(Matrix3d.Scaling(s, basePoint));
            }
            if (args.Number("rotate") is { } r) transforms.Add(Matrix3d.Rotation(Rad(r), Vector3d.ZAxis, basePoint));
            if (args.Point("move") is { } m) transforms.Add(Matrix3d.Displacement(new Vector3d(m[0], m[1], m[2])));
            string? text = args.String("text");
            var attributes = args.Object("attributes");

            int changed = 0;
            foreach (var e in entities)
            {
                Style(db, tr, e, args, createLayers);
                foreach (var t in transforms) e.TransformBy(t);
                if (text is not null)
                {
                    if (e is DBText dt) dt.TextString = text;
                    else if (e is MText mt) mt.Contents = text;
                }
                if (attributes is not null && e is BlockReference br)
                    foreach (ObjectId id in br.AttributeCollection)
                    {
                        var ar = (AttributeReference)tr.GetObject(id, OpenMode.ForWrite);
                        foreach (var p in attributes.Root.EnumerateObject())
                            if (string.Equals(p.Name, ar.Tag, StringComparison.OrdinalIgnoreCase)) ar.TextString = p.Value.ToString();
                    }
                changed++;
            }
            return new ToolOutput
            {
                Data = entities.Select(e => Describe(e, tr, detail: false)).ToList(),
                Summary = $"Changed {changed} entit{(changed == 1 ? "y" : "ies")}. One U in AutoCAD undoes it.",
            };
        });
    }
}

internal sealed class DeleteEntitiesTool : CadTool
{
    public DeleteEntitiesTool() : base("delete_entities") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        var handles = args.Strings("handles");
        if (handles.Count == 0) throw new ToolException("Give one or more handles.");
        return Write(doc, (db, tr) =>
        {
            var skipped = new List<string>();
            int erased = 0;
            foreach (var h in handles)
            {
                var id = ById(db, h);
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity ?? throw new ToolException($"'{h}' is not a drawing entity.");
                var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
                if (layer.IsLocked)
                {
                    skipped.Add($"{h} (layer {layer.Name} is locked)");
                    continue;
                }
                e.UpgradeOpen();
                e.Erase();
                erased++;
            }
            return new ToolOutput
            {
                Data = new Dictionary<string, object?> { ["erased"] = erased, ["skipped"] = skipped },
                Summary = $"Erased {erased}." + (skipped.Count > 0 ? $" Skipped {skipped.Count}: {string.Join(", ", skipped.Take(10))}." : "") + " One U in AutoCAD undoes it.",
            };
        });
    }
}

internal sealed class SetLayerTool : CadTool
{
    public SetLayerTool() : base("set_layer") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        string name = args.RequiredString("name");
        return Write(doc, (db, tr) =>
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            bool existed = table.Has(name);
            var id = Layer(db, tr, name, create: true);
            var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            if (args.String("color") is { Length: > 0 } c)
            {
                var color = ParseColor(c);
                if (color.IsByLayer || color.IsByBlock) throw new ToolException("A layer's colour must be a colour, not ByLayer/ByBlock.");
                layer.Color = color;
            }
            if (args.String("linetype") is { Length: > 0 } lt) layer.LinetypeObjectId = Linetype(db, tr, lt);
            if (args.String("description") is { } d) layer.Description = d;
            if (args.Has("on")) layer.IsOff = !args.Bool("on", true);
            if (args.Has("locked")) layer.IsLocked = args.Bool("locked", false);
            if (args.Has("plot")) layer.IsPlottable = args.Bool("plot", true);
            bool makeCurrent = args.Bool("make_current", false);
            if (args.Has("frozen"))
            {
                bool frozen = args.Bool("frozen", false);
                if (frozen && (id == db.Clayer || makeCurrent)) throw new ToolException("The current layer cannot be frozen.");
                layer.IsFrozen = frozen;
            }
            if (makeCurrent) db.Clayer = id;
            return new ToolOutput { Summary = $"{(existed ? "Changed" : "Created")} layer {layer.Name}." + (makeCurrent ? " It is now the current layer." : "") };
        });
    }
}

/// <summary>Frames entities, an area, or the whole drawing in the view.</summary>
internal sealed class ZoomTool : CadTool
{
    public ZoomTool() : base("zoom") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        var handles = args.Strings("handles");
        if (handles.Count > 0)
            return new ToolOutput { Summary = new AcadSelector().Select(doc, handles, log).Message };

        if (!ReferenceEquals(AcApp.DocumentManager.MdiActiveDocument, doc)) AcApp.DocumentManager.MdiActiveDocument = doc;
        Extents3d extents;
        if (args.Get("window") is { })
        {
            var w = args.Strings("window").Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (w.Length != 4) throw new ToolException("'window' must be [minX, minY, maxX, maxY].");
            extents = new Extents3d(new Point3d(w[0], w[1], 0), new Point3d(w[2], w[3], 0));
        }
        else
        {
            using (AcadDocumentProvider.LockForRead(doc)) doc.Database.UpdateExt(true);
            extents = new Extents3d(doc.Database.Extmin, doc.Database.Extmax);
        }
        AcadSelector.ZoomTo(doc.Editor, extents, log);
        return new ToolOutput { Summary = "Zoomed." };
    }
}

/// <summary>Runs an AutoCAD command or AutoLISP expression, as if typed on the command line.</summary>
internal sealed class RunCommandTool : CadTool
{
    public RunCommandTool() : base("run_command") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        string command = args.RequiredString("command");
        if (!ReferenceEquals(AcApp.DocumentManager.MdiActiveDocument, doc)) AcApp.DocumentManager.MdiActiveDocument = doc;
        if (!command.EndsWith(" ", StringComparison.Ordinal) && !command.EndsWith("\n", StringComparison.Ordinal)) command += "\n";
        log.Info("Assistant command: " + command.Trim());
        doc.SendStringToExecute(command, true, false, true);
        return new ToolOutput { Summary = $"Sent to AutoCAD's command line: {command.Trim()}" };
    }
}

public static class CadTools
{
    /// <summary>Every CAD tool for AI assistants.</summary>
    public static ToolRegistry<Document> Create()
    {
        var tools = new ToolRegistry<Document>();
        foreach (var tool in new CadTool[]
                 {
                     new DrawingInfoTool(), new ListLayersTool(), new ListBlocksTool(), new QueryEntitiesTool(), new GetEntitiesTool(),
                     new GetSelectionTool(), new ViewImageTool(), new CreateEntitiesTool(), new ModifyEntitiesTool(), new DeleteEntitiesTool(),
                     new SetLayerTool(), new ZoomTool(), new RunCommandTool(),
                 })
            tools.Register(tool);
        return tools;
    }
}
