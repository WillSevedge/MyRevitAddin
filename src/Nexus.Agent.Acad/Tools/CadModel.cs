using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Agent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexus.Agent.Acad.Tools;

/// <summary>
/// What the CAD tools share: describing entities compactly for an assistant (handle, type, layer, key geometry),
/// colours, spaces, layers and wildcards. Coordinates are WCS drawing units, angles are degrees.
/// </summary>
internal static class CadModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ types

    /// <summary>Friendly type names (what the assistant uses) → DXF names.</summary>
    public static readonly Dictionary<string, string[]> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["line"] = new[] { "LINE" },
        ["polyline"] = new[] { "LWPOLYLINE", "POLYLINE" },
        ["circle"] = new[] { "CIRCLE" },
        ["arc"] = new[] { "ARC" },
        ["ellipse"] = new[] { "ELLIPSE" },
        ["spline"] = new[] { "SPLINE" },
        ["point"] = new[] { "POINT" },
        ["text"] = new[] { "TEXT" },
        ["mtext"] = new[] { "MTEXT" },
        ["block"] = new[] { "INSERT" },
        ["dimension"] = new[] { "DIMENSION" },
        ["leader"] = new[] { "LEADER", "MULTILEADER" },
        ["hatch"] = new[] { "HATCH" },
        ["table"] = new[] { "ACAD_TABLE" },
        ["viewport"] = new[] { "VIEWPORT" },
        ["solid"] = new[] { "3DSOLID", "SOLID" },
        ["image"] = new[] { "IMAGE" },
    };

    private static readonly Dictionary<string, string> FriendlyOf = Types
        .SelectMany(kv => kv.Value.Select(d => (Dxf: d, Friendly: kv.Key)))
        .GroupBy(x => x.Dxf).ToDictionary(g => g.Key, g => g.First().Friendly, StringComparer.OrdinalIgnoreCase);

    public static string DxfName(Entity e)
    {
        try { return e.GetRXClass().DxfName ?? e.GetType().Name.ToUpperInvariant(); }
        catch { return e.GetType().Name.ToUpperInvariant(); }
    }

    public static string TypeName(Entity e)
    {
        string dxf = DxfName(e);
        return FriendlyOf.TryGetValue(dxf, out var f) ? f : dxf.ToLowerInvariant();
    }

    /// <summary>The DXF names for friendly names or DXF names (["line", "LWPOLYLINE"]).</summary>
    public static HashSet<string> DxfNames(IEnumerable<string> types)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (Types.TryGetValue(t, out var names)) set.UnionWith(names);
            else if (Types.TryGetValue(t.TrimEnd('s'), out names)) set.UnionWith(names); // "lines"
            else set.Add(t);
        }
        return set;
    }

    // ------------------------------------------------------------------ values

    public static double R(double v) => Math.Round(v, 6);
    public static double[] P(Point3d p) => Math.Abs(p.Z) < 1e-9 ? new[] { R(p.X), R(p.Y) } : new[] { R(p.X), R(p.Y), R(p.Z) };
    public static double[] P(Point2d p) => new[] { R(p.X), R(p.Y) };
    public static Point3d Pt(double[] p) => new(p[0], p[1], p.Length > 2 ? p[2] : 0);
    public static double Deg(double radians) => R(radians * 180.0 / Math.PI);
    public static double Rad(double degrees) => degrees * Math.PI / 180.0;
    public static string Hex(ObjectId id) => id.Handle.Value.ToString("X", Inv);

    public static double[]? Box(Entity e)
    {
        try
        {
            var x = e.GeometricExtents;
            return new[] { R(x.MinPoint.X), R(x.MinPoint.Y), R(x.MaxPoint.X), R(x.MaxPoint.Y) };
        }
        catch { return null; }
    }

    /// <summary>"ByLayer", "ByBlock", "1".."255" (ACI) or "r,g,b".</summary>
    public static string ColorText(Color c)
    {
        if (c.IsByLayer) return "ByLayer";
        if (c.IsByBlock) return "ByBlock";
        if (c.ColorMethod == ColorMethod.ByAci) return c.ColorIndex.ToString(Inv);
        return $"{c.Red},{c.Green},{c.Blue}";
    }

    private static readonly Dictionary<string, short> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6, ["white"] = 7, ["black"] = 7,
        ["gray"] = 8, ["grey"] = 8, ["lightgray"] = 9, ["lightgrey"] = 9,
    };

    /// <summary>Parses "ByLayer", "ByBlock", 1-255, a colour name, "r,g,b" or "#rrggbb".</summary>
    public static Color ParseColor(string text)
    {
        string t = text.Trim();
        if (t.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
        if (t.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
        if (ColorNames.TryGetValue(t, out short named)) return Color.FromColorIndex(ColorMethod.ByAci, named);
        if (short.TryParse(t, NumberStyles.Integer, Inv, out short aci) && aci is >= 1 and <= 255) return Color.FromColorIndex(ColorMethod.ByAci, aci);
        if (t.StartsWith("#", StringComparison.Ordinal) && t.Length == 7
            && int.TryParse(t.Substring(1), NumberStyles.HexNumber, Inv, out int rgb))
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
        var parts = t.Split(',');
        if (parts.Length == 3 && parts.All(p => byte.TryParse(p.Trim(), NumberStyles.Integer, Inv, out _)))
            return Color.FromRgb(byte.Parse(parts[0].Trim(), Inv), byte.Parse(parts[1].Trim(), Inv), byte.Parse(parts[2].Trim(), Inv));
        throw new ToolException($"'{text}' is not a colour. Use ByLayer, ByBlock, 1-255 (AutoCAD colour index), a name (red, yellow…), \"r,g,b\" or \"#rrggbb\".");
    }

    // ------------------------------------------------------------------ wildcards

    /// <summary>AutoCAD-style wildcards: * and ?, case-insensitive; several patterns.</summary>
    public static Func<string, bool> Matcher(IReadOnlyCollection<string> patterns)
    {
        if (patterns.Count == 0) return _ => true;
        var regexes = patterns.Select(p => new Regex("^" + Regex.Escape(p.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        return s => regexes.Any(r => r.IsMatch(s));
    }

    // ------------------------------------------------------------------ spaces, layers, handles

    /// <summary>Model space ("model", empty) or the named layout's paper space.</summary>
    public static BlockTableRecord Space(Database db, Transaction tr, string? space, OpenMode mode = OpenMode.ForRead)
    {
        if (string.IsNullOrWhiteSpace(space) || space!.Equals("model", StringComparison.OrdinalIgnoreCase)
            || space.Equals("modelspace", StringComparison.OrdinalIgnoreCase))
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], mode);
        }
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
            if (string.Equals(entry.Key, space, StringComparison.OrdinalIgnoreCase))
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                return (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, mode);
            }
        var names = new List<string>();
        foreach (DBDictionaryEntry entry in layouts) names.Add(entry.Key);
        throw new ToolException($"No layout '{space}'. Use \"model\" or one of: {string.Join(", ", names)}.");
    }

    /// <summary>The layout names in tab order (Model first).</summary>
    public static List<string> Layouts(Database db, Transaction tr)
    {
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var list = new List<(int Order, string Name)>();
        foreach (DBDictionaryEntry entry in layouts)
            list.Add((((Layout)tr.GetObject(entry.Value, OpenMode.ForRead)).TabOrder, entry.Key));
        return list.OrderBy(l => l.Order).Select(l => l.Name).ToList();
    }

    public static ObjectId ById(Database db, string handle)
    {
        string h = handle.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
        if (!long.TryParse(h, NumberStyles.HexNumber, Inv, out long value) || !db.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
            throw new ToolException($"No object with handle '{handle}' (handles are hexadecimal, as query_entities returns them).");
        return id;
    }

    /// <summary>Makes sure a layer exists (created with defaults when <paramref name="create"/>).</summary>
    public static ObjectId Layer(Database db, Transaction tr, string name, bool create)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(name)) return table[name];
        if (!create) throw new ToolException($"No layer '{name}'. Create it with set_layer, or leave out \"layer\" to use the current layer.");
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch { throw new ToolException($"'{name}' is not a valid layer name."); }
        table.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name };
        var id = table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    /// <summary>A linetype by name, loaded from acad.lin when the drawing does not have it yet.</summary>
    public static ObjectId Linetype(Database db, Transaction tr, string name)
    {
        var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(name)) return table[name];
        try { db.LoadLineTypeFile(name, "acad.lin"); }
        catch { /* not in acad.lin */ }
        table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(name)) return table[name];
        throw new ToolException($"No linetype '{name}' in the drawing or in acad.lin.");
    }

    /// <summary>Applies layer, colour and linetype from a tool's item arguments.</summary>
    public static void Style(Database db, Transaction tr, Entity e, ToolArgs a, bool createLayers)
    {
        if (a.String("layer") is { Length: > 0 } layer) e.LayerId = Layer(db, tr, layer, createLayers);
        if (a.String("color") is { Length: > 0 } color) e.Color = ParseColor(color);
        if (a.String("linetype") is { Length: > 0 } lt)
            e.LinetypeId = lt.Equals("bylayer", StringComparison.OrdinalIgnoreCase) || lt.Equals("byblock", StringComparison.OrdinalIgnoreCase)
                ? Linetype(db, tr, lt.Equals("bylayer", StringComparison.OrdinalIgnoreCase) ? "ByLayer" : "ByBlock")
                : Linetype(db, tr, lt);
    }

    // ------------------------------------------------------------------ describing entities

    /// <summary>
    /// The entity for an assistant: handle, type, layer, colour, bounding box and its key geometry or text.
    /// <paramref name="detail"/> adds every vertex, attribute and style.
    /// </summary>
    public static Dictionary<string, object?> Describe(Entity e, Transaction tr, bool detail)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = Hex(e.ObjectId),
            ["type"] = TypeName(e),
            ["layer"] = e.Layer,
        };
        string color = ColorText(e.Color);
        if (detail || color != "ByLayer") d["color"] = color;
        if (detail)
        {
            d["linetype"] = e.Linetype;
            d["lineweight"] = e.LineWeight.ToString();
            d["dxf"] = DxfName(e);
        }

        switch (e)
        {
            case Line l:
                d["start"] = P(l.StartPoint);
                d["end"] = P(l.EndPoint);
                d["length"] = R(l.Length);
                d["angle"] = Deg(l.Angle);
                break;
            case Polyline pl:
            {
                int n = pl.NumberOfVertices;
                int show = detail ? Math.Min(n, 1000) : Math.Min(n, 20);
                d["closed"] = pl.Closed;
                d["vertex_count"] = n;
                d["vertices"] = Enumerable.Range(0, show).Select(i => P(pl.GetPoint2dAt(i))).ToList();
                if (detail)
                {
                    var bulges = Enumerable.Range(0, show).Select(i => R(pl.GetBulgeAt(i))).ToList();
                    if (bulges.Any(b => b != 0)) d["bulges"] = bulges;
                    if (pl.Elevation != 0) d["elevation"] = R(pl.Elevation);
                    if (pl.ConstantWidth != 0) d["width"] = R(pl.ConstantWidth);
                }
                d["length"] = R(pl.Length);
                if (pl.Closed) d["area"] = R(pl.Area);
                break;
            }
            case Circle c:
                d["center"] = P(c.Center);
                d["radius"] = R(c.Radius);
                break;
            case Arc a:
                d["center"] = P(a.Center);
                d["radius"] = R(a.Radius);
                d["start_angle"] = Deg(a.StartAngle);
                d["end_angle"] = Deg(a.EndAngle);
                d["length"] = R(a.Length);
                break;
            case DBPoint p:
                d["position"] = P(p.Position);
                break;
            case DBText t:
                d["text"] = t.TextString;
                d["position"] = P(t.Position);
                d["height"] = R(t.Height);
                if (t.Rotation != 0 || detail) d["rotation"] = Deg(t.Rotation);
                if (detail) d["style"] = StyleName(tr, t.TextStyleId);
                break;
            case MText m:
                d["text"] = Plain(m);
                d["position"] = P(m.Location);
                d["height"] = R(m.TextHeight);
                if (detail)
                {
                    d["width"] = R(m.Width);
                    d["contents"] = m.Contents;
                    d["style"] = StyleName(tr, m.TextStyleId);
                    d["rotation"] = Deg(m.Rotation);
                }
                break;
            case BlockReference br:
            {
                d["block"] = BlockName(br, tr);
                d["position"] = P(br.Position);
                if (br.Rotation != 0 || detail) d["rotation"] = Deg(br.Rotation);
                var s = br.ScaleFactors;
                if (detail || s.X != 1 || s.Y != 1 || s.Z != 1) d["scale"] = new[] { R(s.X), R(s.Y), R(s.Z) };
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in br.AttributeCollection)
                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar && !attributes.ContainsKey(ar.Tag))
                        attributes[ar.Tag] = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString;
                if (attributes.Count > 0) d["attributes"] = attributes;
                if (detail && br.IsDynamicBlock)
                {
                    var props = new Dictionary<string, object?>();
                    foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                        if (p.PropertyName != "Origin" && !props.ContainsKey(p.PropertyName)) props[p.PropertyName] = p.Value?.ToString();
                    if (props.Count > 0) d["dynamic_properties"] = props;
                }
                break;
            }
            case Dimension dim:
                d["dimension_type"] = dim.GetType().Name;
                d["measurement"] = R(SafeMeasurement(dim));
                if (dim.DimensionText.Length > 0) d["text_override"] = dim.DimensionText;
                if (detail) d["style"] = dim.DimensionStyleName;
                break;
            case Hatch h:
                d["pattern"] = h.PatternName;
                try { d["area"] = R(h.Area); } catch { /* self-intersecting boundary */ }
                break;
        }
        d["bbox"] = Box(e);
        return d;
    }

    public static string BlockName(BlockReference br, Transaction tr)
    {
        try
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        catch { return br.Name; }
    }

    private static string StyleName(Transaction tr, ObjectId id)
    {
        try { return ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name; }
        catch { return ""; }
    }

    private static string Plain(MText m)
    {
        try { return m.Text; }
        catch { return m.Contents; }
    }

    private static double SafeMeasurement(Dimension d)
    {
        try { return d.Measurement; }
        catch { return 0; }
    }

    /// <summary>Text an entity shows (text, mtext, attributes, dimension override) for text searches.</summary>
    public static string TextOf(Entity e, Transaction tr)
    {
        switch (e)
        {
            case DBText t: return t.TextString;
            case MText m: return Plain(m);
            case Dimension d: return d.DimensionText;
            case BlockReference br:
                var parts = new List<string>();
                foreach (ObjectId id in br.AttributeCollection)
                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar) parts.Add(ar.TextString);
                return string.Join(" ", parts);
            default: return "";
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Runs <paramref name="work"/> with the drawing locked in one transaction (one undo step in AutoCAD).</summary>
    public static T Write<T>(Document doc, Func<Database, Transaction, T> work)
    {
        if (doc.IsReadOnly) throw new ToolException($"'{Path.GetFileName(doc.Name)}' is open read-only.");
        using var docLock = AcadDocumentProvider.LockForRead(doc);
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var result = work(doc.Database, tr);
        tr.Commit();
        return result;
    }

    public static T Read<T>(Document doc, Func<Database, Transaction, T> work)
    {
        using var docLock = AcadDocumentProvider.LockForRead(doc);
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var result = work(doc.Database, tr);
        tr.Commit();
        return result;
    }
}
