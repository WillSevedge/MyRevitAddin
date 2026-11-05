using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Agent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>
/// Plant 3D tools for AI assistants (offered as autocad_plant3d_*): the open project and its drawings, the Plant 3D
/// objects in a drawing with their project data (tag, line number, size, spec, service…), and changing that data.
/// Values live in Plant 3D's project database and are changed through Plant 3D, as in the Properties palette.
/// </summary>
internal static class PlantTools
{
    /// <summary>The properties listed for every object (all of them with "all_properties").</summary>
    private static readonly string[] Common =
    {
        "Tag", "LineNumberTag", "Size", "Spec", "Service", "PartFamilyLongDesc", "Description", "ShortDescription",
        "Status", "Manufacturer", "Material", "PressureClass", "InsulationType",
    };

    public static void Register(ToolRegistry<Document> tools, PlantApi api)
    {
        tools.Register(new ProjectTool(api));
        tools.Register(new ObjectsTool(api));
        tools.Register(new SetPropertiesTool(api));
    }

    private static object Project(PlantApi api) =>
        api.CurrentProject() ?? throw new ToolException("No Plant 3D project is open. Open the project in Plant 3D's Project Manager (and the drawing from it).");

    private static string Value(List<KeyValuePair<string, string>> props, string name) => PlantObjectsReader.Value(props, name) ?? "";

    private static Func<string, bool> Matcher(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return _ => true;
        var regex = new Regex("^" + Regex.Escape(pattern!.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        return s => regex.IsMatch(s);
    }

    private sealed class ProjectTool : AgentTool<Document>
    {
        private readonly PlantApi _api;

        public ProjectTool(PlantApi api) : base("plant3d_project", "Plant 3D project",
            "The open Plant 3D project: name, settings, parts (Piping, P&ID, Ortho, Iso) and the drawings registered in each part.",
            "{\"type\": \"object\", \"properties\": {\"drawings\": {\"type\": \"boolean\", \"default\": true}}}", readOnly: true) => _api = api;

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
        {
            var project = Project(_api);
            var parts = new List<object>();
            foreach (var name in PlantApi.PartNames)
            {
                var part = _api.Part(project, name);
                if (part is null) continue;
                var drawings = args.Bool("drawings", true)
                    ? _api.Drawings(part).Select(d => PlantApi.ScalarText(d, "AbsoluteFileName") ?? PlantApi.ScalarText(d, "ResolvedFilePath")
                                                      ?? PlantApi.ScalarText(d, "RelativeFileName") ?? PlantApi.ScalarText(d, "FileName") ?? "").Where(f => f.Length > 0).ToList()
                    : null;
                parts.Add(new { part = name == "PnId" ? "P&ID" : name, drawings });
            }
            return new
            {
                name = PlantApi.ScalarText(project, "ProjectName") ?? PlantApi.ScalarText(project, "Name"),
                folder = PlantApi.ScalarText(project, "ProjectDirectory") ?? PlantApi.ScalarText(project, "ProjectFolderPath"),
                settings = PlantApi.Scalars(project).Take(60).ToDictionary(kv => kv.Key, kv => kv.Value),
                parts,
            };
        }
    }

    private sealed class ObjectsTool : AgentTool<Document>
    {
        private readonly PlantApi _api;

        public ObjectsTool(PlantApi api) : base("plant3d_objects", "Plant 3D objects",
            "Plant 3D objects in the drawing (pipes, fittings, valves, equipment, instruments, supports; P&ID symbols and lines) with their project data: " +
            "class, tag, line number, size, spec, service, description. Filter by class (wildcards: \"*Valve*\"), tag, line number, or handles; " +
            "all_properties returns every property. Paged; gaskets, bolts and welds are left out unless connections is true.",
            "{\"type\": \"object\", \"properties\": {\"class\": {\"type\": \"string\"}, \"tag\": {\"type\": \"string\", \"description\": \"wildcards allowed\"}, " +
            "\"line_number\": {\"type\": \"string\"}, \"handles\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}}, " +
            "\"all_properties\": {\"type\": \"boolean\", \"default\": false}, \"connections\": {\"type\": \"boolean\", \"default\": false}, " +
            "\"limit\": {\"type\": \"integer\", \"default\": 200}, \"offset\": {\"type\": \"integer\", \"default\": 0}}}",
            readOnly: true) => _api = api;

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
        {
            var project = Project(_api);
            var cls = Matcher(args.String("class"));
            var tag = Matcher(args.String("tag"));
            var line = Matcher(args.String("line_number"));
            bool all = args.Bool("all_properties", false), connections = args.Bool("connections", false);
            int limit = args.Int("limit", 200, 1, 5000), offset = args.Int("offset", 0, 0);
            var handles = new HashSet<string>(args.Strings("handles").Select(h => h.Trim().TrimStart('0').ToUpperInvariant()));

            using var docLock = AcadDocumentProvider.LockForRead(doc);
            var db = doc.Database;
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            int total = 0;
            var page = new List<object>();
            foreach (ObjectId id in space)
            {
                ct.ThrowIfCancellationRequested();
                string handle = id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                if (handles.Count > 0 && !handles.Contains(handle)) continue;
                var link = _api.Link(project, id);
                if (link is null) continue;
                var (dlm, row) = link.Value;
                var props = _api.Properties(dlm, id, row, visibleOnly: !all);
                string className = _api.ClassName(dlm, id, row) ?? PlantObjectsReader.Value(props, "PnPClassName") ?? id.ObjectClass.DxfName;
                if (!connections && handles.Count == 0 && PlantObjectsReader.ConnectionClasses.Any(c => className.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (!cls(className) || !tag(Value(props, "Tag")) || !line(Value(props, "LineNumberTag"))) continue;
                if (total >= offset && page.Count < limit)
                {
                    var values = all
                        ? props.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value)
                        : Common.Select(n => (n, v: Value(props, n))).Where(x => x.v.Length > 0).ToDictionary(x => x.n, x => x.v);
                    page.Add(new { handle, @class = className, properties = values });
                }
                total++;
            }
            tr.Commit();
            return new ToolOutput
            {
                Data = new { total, offset, returned = page.Count, objects = page },
                Summary = total == 0
                    ? "No Plant 3D objects match (or the drawing is not part of the open project)."
                    : $"{total} object(s) match; returned {page.Count}." + (offset + page.Count < total ? $" Use offset {offset + page.Count} for more." : ""),
            };
        }
    }

    private sealed class SetPropertiesTool : AgentTool<Document>
    {
        private readonly PlantApi _api;

        public SetPropertiesTool(PlantApi api) : base("plant3d_set_properties", "Change Plant 3D data",
            "Set Plant 3D properties (as in the Properties palette) on objects by handle: e.g. {\"Service\": \"CW\", \"Status\": \"New\"}. " +
            "Reports each value after the change; Plant 3D keeps calculated values (e.g. a tag built from its parts) as they are.",
            "{\"type\": \"object\", \"properties\": {\"handles\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}}, " +
            "\"properties\": {\"type\": \"object\", \"additionalProperties\": {\"type\": \"string\"}}}, \"required\": [\"handles\", \"properties\"]}",
            readOnly: false) => _api = api;

        public override object? Call(Document doc, ToolArgs args, AgentLog log, CancellationToken ct)
        {
            var project = Project(_api);
            var handles = args.Strings("handles");
            var values = args.Object("properties")?.Root.EnumerateObject().Select(p => (p.Name, Value: p.Value.ToString())).ToList()
                         ?? throw new ToolException("Give the properties to set, e.g. {\"Service\": \"CW\"}.");
            if (handles.Count == 0 || values.Count == 0) throw new ToolException("Give handles and properties.");
            if (doc.IsReadOnly) throw new ToolException("The drawing is open read-only.");

            using var docLock = AcadDocumentProvider.LockForRead(doc);
            var results = new List<object>();
            int updated = 0;
            foreach (var h in handles)
            {
                if (!long.TryParse(h.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv)
                    || !doc.Database.TryGetObjectId(new Handle(hv), out var id) || id.IsErased)
                {
                    results.Add(new { handle = h, error = "No such object." });
                    continue;
                }
                var link = _api.Link(project, id);
                if (link is null)
                {
                    results.Add(new { handle = h, error = "Not a Plant 3D object in the open project." });
                    continue;
                }
                var (dlm, row) = link.Value;
                try
                {
                    _api.SetProperties(dlm, id, row, values.Select(v => v.Name).ToList(), values.Select(v => v.Value).ToList());
                    var after = _api.Properties(dlm, id, row, visibleOnly: false);
                    results.Add(new { handle = h, now = values.ToDictionary(v => v.Name, v => PlantObjectsReader.Value(after, v.Name)) });
                    updated++;
                }
                catch (Exception ex)
                {
                    results.Add(new { handle = h, error = ex.Message });
                }
            }
            return new ToolOutput { Data = results, Summary = $"Updated {updated} of {handles.Count} object(s)." };
        }
    }
}
