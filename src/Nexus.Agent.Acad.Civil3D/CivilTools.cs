using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Agent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Surface = Autodesk.Civil.DatabaseServices.Surface;

namespace Nexus.Agent.Acad.Civil3D;

/// <summary>
/// Civil 3D tools for AI assistants (offered as autocad_civil3d_*): what the drawing holds, alignments and profiles,
/// station/offset, surfaces and their elevations, pipe networks, COGO points, corridors, and adding COGO points.
/// Distances are drawing units, slopes are as Civil 3D reports them (rise/run).
/// </summary>
internal static class CivilTools
{
    public static void Register(ToolRegistry<Document> tools)
    {
        tools.Register(new OverviewTool());
        tools.Register(new AlignmentsTool());
        tools.Register(new StationOffsetTool());
        tools.Register(new SurfacesTool());
        tools.Register(new SurfaceElevationTool());
        tools.Register(new PipeNetworksTool());
        tools.Register(new PointsTool());
        tools.Register(new AddPointsTool());
        tools.Register(new CorridorsTool());
    }

    // ------------------------------------------------------------------ shared

    internal static double R(double v) => Math.Round(v, 4);
    internal static double[] P(Point3d p) => new[] { R(p.X), R(p.Y), R(p.Z) };
    internal static string Hex(ObjectId id) => id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

    internal static T Read<T>(Document doc, Func<CivilDocument, Transaction, T> work)
    {
        using var docLock = AcadDocumentProvider.LockForRead(doc);
        var civil = CivilDocument.GetCivilDocument(doc.Database)
                    ?? throw new ToolException("This drawing has no Civil 3D data (open it in Civil 3D).");
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var result = work(civil, tr);
        tr.Commit();
        return result;
    }

    internal static string NameOf(Transaction tr, ObjectId id)
    {
        if (id.IsNull) return "";
        try { return tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Entity e ? e.Name : ""; }
        catch { return ""; }
    }

    internal static Func<string, bool> Matcher(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return _ => true;
        var regex = new Regex("^" + Regex.Escape(pattern!.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        return s => regex.IsMatch(s);
    }

    /// <summary>An alignment by name (exact, then wildcard/contains).</summary>
    internal static Alignment FindAlignment(CivilDocument civil, Transaction tr, string name)
    {
        var all = civil.GetAlignmentIds().Cast<ObjectId>().Select(id => (Alignment)tr.GetObject(id, OpenMode.ForRead)).ToList();
        return all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(a => a.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? throw new ToolException($"No alignment '{name}'. Alignments: {string.Join(", ", all.Select(a => a.Name).Take(30))}.");
    }

    internal static Surface FindSurface(CivilDocument civil, Transaction tr, string name)
    {
        var all = civil.GetSurfaceIds().Cast<ObjectId>().Select(id => (Surface)tr.GetObject(id, OpenMode.ForRead)).ToList();
        return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? throw new ToolException($"No surface '{name}'. Surfaces: {string.Join(", ", all.Select(s => s.Name).Take(30))}.");
    }

    private static T? Try<T>(Func<T> get) where T : struct
    {
        try { return get(); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ tools

    private sealed class OverviewTool : AgentTool<Document>
    {
        public OverviewTool() : base("civil3d_overview", "Civil 3D overview",
            "What Civil 3D objects the drawing holds: alignments (and profiles), surfaces, pipe networks (pipes, structures), corridors, sites, point groups and COGO points. Start here in Civil 3D.",
            "{\"type\": \"object\", \"properties\": {}}", readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var alignments = civil.GetAlignmentIds().Cast<ObjectId>().Select(id => (Alignment)tr.GetObject(id, OpenMode.ForRead)).ToList();
            var networks = civil.GetPipeNetworkIds().Cast<ObjectId>().Select(id => (Network)tr.GetObject(id, OpenMode.ForRead)).ToList();
            var groups = new List<object>();
            foreach (ObjectId id in civil.PointGroups)
                if (tr.GetObject(id, OpenMode.ForRead) is PointGroup g) groups.Add(new { name = g.Name, points = g.PointsCount });
            return new Dictionary<string, object?>
            {
                ["alignments"] = alignments.Select(a => a.Name).ToList(),
                ["profiles"] = alignments.Sum(a => a.GetProfileIds().Count),
                ["surfaces"] = civil.GetSurfaceIds().Cast<ObjectId>().Select(id => NameOf(tr, id)).ToList(),
                ["pipe_networks"] = networks.Select(n => new { name = n.Name, pipes = n.GetPipeIds().Count, structures = n.GetStructureIds().Count }).ToList(),
                ["corridors"] = civil.CorridorCollection.Cast<ObjectId>().Select(id => NameOf(tr, id)).ToList(),
                ["sites"] = civil.GetSiteIds().Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead) is Site s ? s.Name : "").ToList(),
                ["point_groups"] = groups,
                ["cogo_points"] = civil.CogoPoints.Count,
            };
        });
    }

    private sealed class AlignmentsTool : AgentTool<Document>
    {
        public AlignmentsTool() : base("civil3d_alignments", "Alignments and profiles",
            "Alignments with type, length, start/end station, site, style and their profiles (type, station range, min/max elevation). Optional: name (wildcards), and station to get each profile's elevation and grade there.",
            "{\"type\": \"object\", \"properties\": {\"name\": {\"type\": \"string\"}, \"station\": {\"type\": \"number\", \"description\": \"Also report each profile's elevation and grade at this station.\"}}}",
            readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var match = Matcher(args.String("name"));
            double? station = args.Number("station");
            var result = new List<object>();
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                var a = (Alignment)tr.GetObject(id, OpenMode.ForRead);
                if (!match(a.Name)) continue;
                var profiles = new List<object>();
                foreach (ObjectId pid in a.GetProfileIds())
                {
                    var p = (Profile)tr.GetObject(pid, OpenMode.ForRead);
                    var d = new Dictionary<string, object?>
                    {
                        ["name"] = p.Name, ["type"] = p.ProfileType.ToString(),
                        ["start_station"] = R(p.StartingStation), ["end_station"] = R(p.EndingStation),
                        ["min_elevation"] = Try(() => R(p.ElevationMin)), ["max_elevation"] = Try(() => R(p.ElevationMax)),
                    };
                    if (station is { } st && st >= p.StartingStation && st <= p.EndingStation)
                    {
                        d["elevation_at_station"] = Try(() => R(p.ElevationAt(st)));
                        d["grade_at_station"] = Try(() => Math.Round(p.GradeAt(st), 6));
                    }
                    profiles.Add(d);
                }
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = a.Name, ["handle"] = Hex(a.ObjectId), ["type"] = a.AlignmentType.ToString(), ["length"] = R(a.Length),
                    ["start_station"] = R(a.StartingStation), ["end_station"] = R(a.EndingStation), ["site"] = a.SiteName,
                    ["style"] = a.StyleName, ["layer"] = a.Layer, ["profiles"] = profiles,
                });
            }
            return result;
        });
    }

    private sealed class StationOffsetTool : AgentTool<Document>
    {
        public StationOffsetTool() : base("civil3d_station_offset", "Station and offset",
            "Along an alignment: give points [x, y] to get their station and offset (left negative), or stations (with optional offsets) to get the points. Several at once.",
            "{\"type\": \"object\", \"properties\": {\"alignment\": {\"type\": \"string\"}, " +
            "\"points\": {\"type\": \"array\", \"items\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}}, " +
            "\"stations\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"offsets\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}, \"description\": \"one per station (default 0)\"}}, " +
            "\"required\": [\"alignment\"]}", readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var a = FindAlignment(civil, tr, args.RequiredString("alignment"));
            var result = new List<object>();
            foreach (var p in args.Points("points"))
            {
                double station = 0, offset = 0;
                try
                {
                    a.StationOffset(p[0], p[1], ref station, ref offset);
                    result.Add(new { point = new[] { p[0], p[1] }, station = R(station), offset = R(offset) });
                }
                catch (Exception ex) { result.Add(new { point = new[] { p[0], p[1] }, error = "Off the alignment: " + ex.Message }); }
            }
            var offsets = args.Strings("offsets").Select(o => double.Parse(o, CultureInfo.InvariantCulture)).ToList();
            var stations = args.Strings("stations").Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
            for (int i = 0; i < stations.Count; i++)
            {
                double x = 0, y = 0, off = i < offsets.Count ? offsets[i] : 0;
                try
                {
                    a.PointLocation(stations[i], off, ref x, ref y);
                    result.Add(new { station = stations[i], offset = off, point = new[] { R(x), R(y) } });
                }
                catch (Exception ex) { result.Add(new { station = stations[i], offset = off, error = $"Station outside {R(a.StartingStation)}–{R(a.EndingStation)}: {ex.Message}" }); }
            }
            if (result.Count == 0) throw new ToolException("Give points or stations.");
            return new ToolOutput { Data = result, Summary = $"Alignment {a.Name}, stations {R(a.StartingStation)} to {R(a.EndingStation)}." };
        });
    }

    private sealed class SurfacesTool : AgentTool<Document>
    {
        public SurfacesTool() : base("civil3d_surfaces", "Surfaces",
            "Surfaces with type, number of points, minimum/maximum/mean elevation, extents, 2D and 3D area and slope range (terrain surfaces).",
            "{\"type\": \"object\", \"properties\": {\"name\": {\"type\": \"string\"}}}", readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var match = Matcher(args.String("name"));
            var result = new List<object>();
            foreach (ObjectId id in civil.GetSurfaceIds())
            {
                var s = (Surface)tr.GetObject(id, OpenMode.ForRead);
                if (!match(s.Name)) continue;
                var d = new Dictionary<string, object?> { ["name"] = s.Name, ["handle"] = Hex(s.ObjectId), ["type"] = s.GetType().Name, ["style"] = s.StyleName };
                try
                {
                    var g = s.GetGeneralProperties();
                    d["points"] = g.NumberOfPoints;
                    d["min_elevation"] = R(g.MinimumElevation);
                    d["max_elevation"] = R(g.MaximumElevation);
                    d["mean_elevation"] = R(g.MeanElevation);
                    d["extents"] = new[] { R(g.MinimumCoordinateX), R(g.MinimumCoordinateY), R(g.MaximumCoordinateX), R(g.MaximumCoordinateY) };
                }
                catch (Exception ex) { d["error"] = ex.Message; }
                TerrainSurfaceProperties? t = null;
                try { t = s is TinSurface tin ? tin.GetTerrainProperties() : s is GridSurface grid ? grid.GetTerrainProperties() : null; }
                catch { /* volume surfaces */ }
                if (t is not null)
                {
                    d["area_2d"] = R(t.SurfaceArea2D);
                    d["area_3d"] = R(t.SurfaceArea3D);
                    d["slope_min_mean_max"] = new[] { R(t.MinimumGradeOrSlope), R(t.MeanGradeOrSlope), R(t.MaximumGradeOrSlope) };
                }
                result.Add(d);
            }
            return result;
        });
    }

    private sealed class SurfaceElevationTool : AgentTool<Document>
    {
        public SurfaceElevationTool() : base("civil3d_surface_elevation", "Surface elevation at points",
            "Elevation of a surface at each point [x, y] (null where the point is outside the surface). Up to 5000 points.",
            "{\"type\": \"object\", \"properties\": {\"surface\": {\"type\": \"string\"}, \"points\": {\"type\": \"array\", \"items\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}}}, \"required\": [\"surface\", \"points\"]}",
            readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var s = FindSurface(civil, tr, args.RequiredString("surface"));
            var points = args.Points("points");
            if (points.Count == 0 || points.Count > 5000) throw new ToolException("Give 1 to 5000 points.");
            return points.Select(p => new { point = new[] { p[0], p[1] }, elevation = Try(() => R(s.FindElevationAtXY(p[0], p[1]))) }).ToList();
        });
    }

    private sealed class PipeNetworksTool : AgentTool<Document>
    {
        public PipeNetworksTool() : base("civil3d_pipe_networks", "Pipe networks",
            "Gravity pipe networks with their pipes (size, length, slope, start/end structure, cover) and structures (rim and sump elevation, position, connected pipes). Optional: network name; limit per network (default 500).",
            "{\"type\": \"object\", \"properties\": {\"network\": {\"type\": \"string\"}, \"limit\": {\"type\": \"integer\", \"default\": 500}}}", readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var match = Matcher(args.String("network"));
            int limit = args.Int("limit", 500, 1, 5000);
            var result = new List<object>();
            foreach (ObjectId id in civil.GetPipeNetworkIds())
            {
                var n = (Network)tr.GetObject(id, OpenMode.ForRead);
                if (!match(n.Name)) continue;
                var pipes = n.GetPipeIds().Cast<ObjectId>().Take(limit).Select(pid => (Pipe)tr.GetObject(pid, OpenMode.ForRead)).Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name, ["handle"] = Hex(p.ObjectId), ["size"] = p.PartSizeName,
                    ["inner_diameter"] = R(p.InnerDiameterOrWidth), ["length_2d"] = R(p.Length2D), ["slope"] = Math.Round(p.Slope, 6),
                    ["start"] = P(p.StartPoint), ["end"] = P(p.EndPoint),
                    ["start_structure"] = NameOf(tr, p.StartStructureId), ["end_structure"] = NameOf(tr, p.EndStructureId),
                    ["cover_start"] = Try(() => R(p.CoverOfStartPoint)), ["cover_end"] = Try(() => R(p.CoverOfEndpoint)),
                }).ToList();
                var structures = n.GetStructureIds().Cast<ObjectId>().Take(limit).Select(sid => (Structure)tr.GetObject(sid, OpenMode.ForRead)).Select(s => new Dictionary<string, object?>
                {
                    ["name"] = s.Name, ["handle"] = Hex(s.ObjectId), ["size"] = s.PartSizeName, ["position"] = P(s.Location),
                    ["rim_elevation"] = Try(() => R(s.RimElevation)), ["sump_elevation"] = Try(() => R(s.SumpElevation)),
                    ["connected_pipes"] = s.ConnectedPipesCount,
                }).ToList();
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = n.Name, ["parts_list"] = n.PartsListName, ["reference_surface"] = n.ReferenceSurfaceName,
                    ["pipes"] = pipes, ["structures"] = structures,
                });
            }
            return result;
        });
    }

    private sealed class PointsTool : AgentTool<Document>
    {
        public PointsTool() : base("civil3d_points", "COGO points",
            "COGO points: number, name, easting, northing, elevation, raw and full description. Filter by point group, description (wildcards, e.g. \"TREE*\") or number range; paged.",
            "{\"type\": \"object\", \"properties\": {\"group\": {\"type\": \"string\"}, \"description\": {\"type\": \"string\"}, " +
            "\"from_number\": {\"type\": \"integer\"}, \"to_number\": {\"type\": \"integer\"}, \"limit\": {\"type\": \"integer\", \"default\": 200}, \"offset\": {\"type\": \"integer\", \"default\": 0}}}",
            readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            HashSet<uint>? inGroup = null;
            if (args.String("group") is { Length: > 0 } groupName)
            {
                PointGroup? group = null;
                foreach (ObjectId id in civil.PointGroups)
                    if (tr.GetObject(id, OpenMode.ForRead) is PointGroup g && string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)) group = g;
                if (group is null) throw new ToolException($"No point group '{groupName}'.");
                inGroup = new HashSet<uint>(group.GetPointNumbers());
            }
            var description = Matcher(args.String("description"));
            int from = args.Int("from_number", 0, 0), to = args.Int("to_number", int.MaxValue, 0);
            int limit = args.Int("limit", 200, 1, 5000), offset = args.Int("offset", 0, 0);
            int total = 0;
            var page = new List<object>();
            foreach (ObjectId id in civil.CogoPoints)
            {
                ct.ThrowIfCancellationRequested();
                var p = (CogoPoint)tr.GetObject(id, OpenMode.ForRead);
                if (p.PointNumber < from || p.PointNumber > to) continue;
                if (inGroup is not null && !inGroup.Contains(p.PointNumber)) continue;
                if (!description(p.RawDescription) && !description(p.FullDescription)) continue;
                if (total >= offset && page.Count < limit)
                    page.Add(new { number = p.PointNumber, name = p.PointName, easting = R(p.Easting), northing = R(p.Northing), elevation = R(p.Elevation),
                                   raw_description = p.RawDescription, full_description = p.FullDescription, handle = Hex(id) });
                total++;
            }
            return new ToolOutput
            {
                Data = new { total, offset, returned = page.Count, points = page },
                Summary = $"{total} point(s) match; returned {page.Count}." + (offset + page.Count < total ? $" Use offset {offset + page.Count} for more." : ""),
            };
        });
    }

    private sealed class AddPointsTool : AgentTool<Document>
    {
        public AddPointsTool() : base("civil3d_add_points", "Add COGO points",
            "Create COGO points at [x, y, z] with a raw description (description keys apply as in Civil 3D). One undo step. Returns the new point numbers.",
            "{\"type\": \"object\", \"properties\": {\"points\": {\"type\": \"array\", \"items\": {\"type\": \"object\", \"properties\": {" +
            "\"position\": {\"type\": \"array\", \"items\": {\"type\": \"number\"}}, \"description\": {\"type\": \"string\"}}, \"required\": [\"position\"]}}}, \"required\": [\"points\"]}",
            readOnly: false) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
        {
            var items = args.Objects("points");
            if (items.Count == 0 || items.Count > 10000) throw new ToolException("Give 1 to 10000 points.");
            if (doc.IsReadOnly) throw new ToolException("The drawing is open read-only.");
            using var docLock = AcadDocumentProvider.LockForRead(doc);
            var civil = CivilDocument.GetCivilDocument(doc.Database) ?? throw new ToolException("This drawing has no Civil 3D data.");
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var created = new List<object>();
            foreach (var item in items)
            {
                var p = item.RequiredPoint("position");
                var id = civil.CogoPoints.Add(new Point3d(p[0], p[1], p[2]), item.String("description") ?? "", true);
                var point = (CogoPoint)tr.GetObject(id, OpenMode.ForRead);
                created.Add(new { number = point.PointNumber, handle = Hex(id) });
            }
            tr.Commit();
            return new ToolOutput { Data = created, Summary = $"Added {created.Count} COGO point(s). One U in Civil 3D undoes it." };
        }
    }

    private sealed class CorridorsTool : AgentTool<Document>
    {
        public CorridorsTool() : base("civil3d_corridors", "Corridors",
            "Corridors with their baselines (alignment, profile, station range) and whether they are out of date.",
            "{\"type\": \"object\", \"properties\": {}}", readOnly: true) { }

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct) => Read(doc, (civil, tr) =>
        {
            var result = new List<object>();
            foreach (ObjectId id in civil.CorridorCollection)
            {
                var c = (Corridor)tr.GetObject(id, OpenMode.ForRead);
                var baselines = new List<object>();
                foreach (Baseline b in c.Baselines)
                {
                    double? start = Try(() => b.StartStation), end = Try(() => b.EndStation);
                    baselines.Add(new { name = b.Name, alignment = NameOf(tr, b.AlignmentId), profile = NameOf(tr, b.ProfileId), start_station = start, end_station = end });
                }
                result.Add(new { name = c.Name, handle = Hex(c.ObjectId), out_of_date = c.IsOutOfDate, baselines });
            }
            return result;
        });
    }
}
