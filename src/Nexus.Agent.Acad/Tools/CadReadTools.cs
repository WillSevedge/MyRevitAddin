using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using static Nexus.Agent.Acad.Tools.CadModel;

namespace Nexus.Agent.Acad.Tools;

/// <summary>A CAD tool: its descriptor and its work on the drawing (AutoCAD's main thread, application context).</summary>
internal abstract class CadTool : IAgentTool<Document>
{
    protected CadTool(string name) => Descriptor = CadToolCatalog.Get(name);

    public ToolDescriptor Descriptor { get; }
    public abstract object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct);


}

/// <summary>The drawing at a glance.</summary>
internal sealed class DrawingInfoTool : CadTool
{
    public DrawingInfoTool() : base("drawing_info") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (db, tr) =>
    {
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var model = Space(db, tr, "model");
        foreach (ObjectId id in model)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
            string t = TypeName(e);
            counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        }
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        int blockCount = 0;
        var xrefs = new List<string>();
        foreach (ObjectId id in blocks)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout || btr.IsAnonymous) continue;
            if (btr.IsFromExternalReference) xrefs.Add(btr.Name);
            else blockCount++;
        }
        int layerCount = 0;
        foreach (ObjectId _ in layers) layerCount++;
        string? current = null;
        if (ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument))
            try { current = LayoutManager.Current.CurrentLayout; } catch { /* no layout manager */ }
        return new Dictionary<string, object?>
        {
            ["file"] = Path.GetFileName(doc.Name),
            ["path"] = doc.Name,
            ["units"] = db.Insunits.ToString(),
            ["extents"] = new[] { R(db.Extmin.X), R(db.Extmin.Y), R(db.Extmax.X), R(db.Extmax.Y) },
            ["layouts"] = Layouts(db, tr),
            ["current_layout"] = current,
            ["current_layer"] = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name,
            ["model_space_entities"] = counts,
            ["layers"] = layerCount,
            ["blocks"] = blockCount,
            ["xrefs"] = xrefs,
            ["read_only"] = doc.IsReadOnly,
        };
    });
}

internal sealed class ListLayersTool : CadTool
{
    public ListLayersTool() : base("list_layers") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (db, tr) =>
    {
        var match = Matcher(args.Strings("name"));
        var used = new Dictionary<ObjectId, int>();
        foreach (ObjectId id in Space(db, tr, "model"))
            if (tr.GetObject(id, OpenMode.ForRead) is Entity e) used[e.LayerId] = used.TryGetValue(e.LayerId, out int c) ? c + 1 : 1;
        var result = new List<object>();
        foreach (ObjectId id in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!match(l.Name)) continue;
            string linetype = "";
            try { linetype = ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name; } catch { /* ignored */ }
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = l.Name, ["color"] = ColorText(l.Color), ["linetype"] = linetype, ["on"] = !l.IsOff,
                ["frozen"] = l.IsFrozen, ["locked"] = l.IsLocked, ["plot"] = l.IsPlottable, ["current"] = id == db.Clayer,
                ["entities"] = used.TryGetValue(id, out int n) ? n : 0,
                ["description"] = string.IsNullOrEmpty(l.Description) ? null : l.Description,
            });
        }
        return result;
    });
}

internal sealed class ListBlocksTool : CadTool
{
    public ListBlocksTool() : base("list_blocks") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (db, tr) =>
    {
        var match = Matcher(args.Strings("name"));
        bool xrefs = args.Bool("include_xrefs", true);
        var result = new List<object>();
        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout || btr.IsAnonymous || !match(btr.Name) || (!xrefs && btr.IsFromExternalReference)) continue;
            int refs = btr.GetBlockReferenceIds(true, false).Count;
            if (btr.IsDynamicBlock)
                foreach (ObjectId anon in btr.GetAnonymousBlockIds())
                    refs += ((BlockTableRecord)tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count;
            var tags = new List<string>();
            if (btr.HasAttributeDefinitions)
                foreach (ObjectId e in btr)
                    if (tr.GetObject(e, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant) tags.Add(ad.Tag);
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = btr.Name, ["references"] = refs, ["attributes"] = tags, ["dynamic"] = btr.IsDynamicBlock,
                ["xref"] = btr.IsFromExternalReference ? btr.PathName : null,
                ["description"] = string.IsNullOrEmpty(btr.Comments) ? null : btr.Comments,
            });
        }
        return result;
    });
}

/// <summary>Finds entities by type, layer, block, text and area.</summary>
internal sealed class QueryEntitiesTool : CadTool
{
    public QueryEntitiesTool() : base("query_entities") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (db, tr) =>
    {
        var types = DxfNames(args.Strings("types"));
        var layer = Matcher(args.Strings("layers"));
        var blockNames = args.Strings("blocks");
        var block = Matcher(blockNames);
        string? text = args.String("text_contains");
        var window = args.Get("window") is { } w ? args.Strings("window").Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
        if (window is not null && window.Length != 4) throw new ToolException("'window' must be [minX, minY, maxX, maxY].");
        int limit = args.Int("limit", 100, 1, 1000);
        int offset = args.Int("offset", 0, 0);

        var space = Space(db, tr, args.String("space"));
        int total = 0;
        var page = new List<object>();
        foreach (ObjectId id in space)
        {
            ct.ThrowIfCancellationRequested();
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
            if (types.Count > 0 && !types.Contains(DxfName(e))) continue;
            if (!layer(e.Layer)) continue;
            if (blockNames.Count > 0 && (e is not BlockReference br || !block(BlockName(br, tr)))) continue;
            if (!string.IsNullOrEmpty(text) && TextOf(e, tr).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (window is not null)
            {
                var box = Box(e);
                if (box is null || box[2] < window[0] || box[0] > window[2] || box[3] < window[1] || box[1] > window[3]) continue;
            }
            if (total >= offset && page.Count < limit) page.Add(Describe(e, tr, detail: false));
            total++;
        }
        return new ToolOutput
        {
            Data = new Dictionary<string, object?> { ["space"] = space.IsLayout ? "layout" : "model", ["total"] = total, ["offset"] = offset, ["returned"] = page.Count, ["entities"] = page },
            Summary = total == 0 ? "No entities match." : $"{total} match; returned {page.Count} from {offset}." + (offset + page.Count < total ? $" Use offset {offset + page.Count} for more." : ""),
        };
    });
}

internal sealed class GetEntitiesTool : CadTool
{
    public GetEntitiesTool() : base("get_entities") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (db, tr) =>
    {
        var handles = args.Strings("handles");
        if (handles.Count == 0) throw new ToolException("Give one or more handles.");
        if (handles.Count > 200) throw new ToolException("At most 200 handles at a time.");
        return handles.Select(h => tr.GetObject(ById(db, h), OpenMode.ForRead) is Entity e
            ? (object)Describe(e, tr, detail: true)
            : new Dictionary<string, object?> { ["handle"] = h, ["error"] = "Not a drawing entity." }).ToList();
    });
}

/// <summary>What the user has selected in AutoCAD (grips).</summary>
internal sealed class GetSelectionTool : CadTool
{
    public GetSelectionTool() : base("get_selection") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        PromptSelectionResult picked;
        try { picked = doc.Editor.SelectImplied(); }
        catch (Exception ex) { throw new ToolException("AutoCAD did not give the selection: " + ex.Message); }
        if (picked.Status != PromptStatus.OK || picked.Value is null)
            return new ToolOutput { Data = new List<object>(), Summary = "Nothing is selected in AutoCAD. Ask the user to select objects (without a command running), then try again." };
        var ids = picked.Value.GetObjectIds();
        return Read(doc, (db, tr) => new ToolOutput
        {
            Data = ids.Take(500).Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Entity>().Select(e => Describe(e, tr, detail: false)).ToList(),
            Summary = $"{ids.Length} selected" + (ids.Length > 500 ? " (first 500 shown)." : "."),
        });
    }
}

/// <summary>A picture of the drawing area, so the assistant can see what it drew.</summary>
internal sealed class ViewImageTool : CadTool
{
    public ViewImageTool() : base("view_image") { }

    public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
    {
        if (!ReferenceEquals(AcApp.DocumentManager.MdiActiveDocument, doc)) AcApp.DocumentManager.MdiActiveDocument = doc;
        uint width = (uint)args.Int("width", 1280, 64, 2048);
        uint height = (uint)args.Int("height", 800, 64, 2048);
        using var bitmap = doc.CapturePreviewImage(width, height);
        if (bitmap is null) throw new ToolException("AutoCAD could not capture the view.");
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return new ToolOutput { Png = png.ToArray(), Summary = $"View of {Path.GetFileName(doc.Name)} ({width}×{height})." };
    }
}
