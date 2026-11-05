using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Agent;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Plant3D;

/// <summary>
/// The parts of the Plant 3D API Nexus uses, reached by reflection (late binding):
/// PlantApplication.CurrentProject, its project parts (Piping, PnId, Ortho, Iso) and each part's
/// DataLinksManager (FindAcPpRowId, GetAllProperties, SetProperties), the same calls a compiled
/// add-in makes. Late binding keeps this module buildable without Plant 3D and working across
/// Plant 3D versions. Every call fails soft: a missing member is reported, never thrown at AutoCAD.
/// </summary>
internal sealed class PlantApi
{
    /// <summary>Project parts in the order drawings are looked up.</summary>
    public static readonly string[] PartNames = { "Piping", "PnId", "Ortho", "Iso" };

    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private readonly AgentLog _log;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    // Database → project part whose DataLinksManager knows its objects.
    private readonly ConditionalWeakTable<Database, string> _partOfDrawing = new();

    public PlantApi(AgentLog log) => _log = log;

    // ------------------------------------------------------------------ project

    /// <summary>The project open in Project Manager, or null.</summary>
    public object? CurrentProject()
    {
        var type = FindType("Autodesk.ProcessPower.PlantInstance.PlantApplication", "PnPProjectManagerMgd");
        if (type is null)
        {
            Report("PlantApplication", "Plant 3D's project API (PnPProjectManagerMgd) is not loaded.");
            return null;
        }
        return Try(() => type.GetProperty("CurrentProject", BindingFlags.Public | BindingFlags.Static)?.GetValue(null), "CurrentProject");
    }

    /// <summary>A project part ("Piping", "PnId", "Ortho", "Iso"), or null when the project has none.</summary>
    public object? Part(object project, string name)
    {
        var parts = Try(() => project.GetType().GetProperty("ProjectParts", Instance)?.GetValue(project), "ProjectParts");
        if (parts is null) return null;
        var indexer = parts.GetType().GetProperties(Instance)
            .FirstOrDefault(p => p.GetIndexParameters() is { Length: 1 } ip && ip[0].ParameterType == typeof(string));
        if (indexer is not null)
        {
            try { return indexer.GetValue(parts, new object[] { name }); }
            catch { return null; } // the project has no such part
        }
        if (parts is IEnumerable list)
            foreach (var p in list)
                if (p is not null && string.Equals(ScalarText(p, "ProjectPartName") ?? ScalarText(p, "Name"), name, StringComparison.OrdinalIgnoreCase))
                    return p;
        return null;
    }

    public object? DataLinksManager(object part) =>
        Try(() => part.GetType().GetProperty("DataLinksManager", Instance)?.GetValue(part), "DataLinksManager");

    /// <summary>The drawings registered in a project part (PnPProjectDrawing objects).</summary>
    public List<object> Drawings(object part)
    {
        var method = part.GetType().GetMethods(Instance).FirstOrDefault(m => m.Name == "GetPnPDrawingFiles" && m.GetParameters().Length == 0);
        if (method is null)
        {
            Report("GetPnPDrawingFiles", "This Plant 3D version does not list project drawings (GetPnPDrawingFiles).");
            return new List<object>();
        }
        var result = Try(() => method.Invoke(part, null), "GetPnPDrawingFiles");
        return result is IEnumerable e ? e.Cast<object>().Where(o => o is not null).ToList() : new List<object>();
    }

    // ------------------------------------------------------------------ objects

    /// <summary>
    /// The DataLinksManager that knows <paramref name="id"/>'s drawing, and the object's row in the project
    /// database; null when the object is not a Plant 3D object (or the drawing is not in the project).
    /// </summary>
    public (object Dlm, int RowId)? Link(object project, ObjectId id)
    {
        var db = id.Database;
        if (db is not null && _partOfDrawing.TryGetValue(db, out var known))
            return LinkIn(project, known, id);

        foreach (var name in PartNames)
        {
            var link = LinkIn(project, name, id);
            if (link is null) continue;
            if (db is not null)
            {
                // ConditionalWeakTable.AddOrUpdate does not exist on .NET Framework (2024).
                _partOfDrawing.Remove(db);
                _partOfDrawing.Add(db, name);
            }
            return link;
        }
        return null;
    }

    private (object Dlm, int RowId)? LinkIn(object project, string partName, ObjectId id)
    {
        var part = Part(project, partName);
        var dlm = part is null ? null : DataLinksManager(part);
        if (dlm is null) return null;
        int row = RowId(dlm, id);
        return row > 0 ? (dlm, row) : null;
    }

    public int RowId(object dlm, ObjectId id)
    {
        var method = dlm.GetType().GetMethod("FindAcPpRowId", Instance, null, new[] { typeof(ObjectId) }, null);
        if (method is null)
        {
            Report("FindAcPpRowId", "This Plant 3D version has no DataLinksManager.FindAcPpRowId(ObjectId).");
            return -1;
        }
        try { return method.Invoke(dlm, new object[] { id }) is int row ? row : -1; }
        catch { return -1; } // not linked
    }

    /// <summary>All Plant 3D properties of an object (as in the Properties palette when <paramref name="visibleOnly"/>).</summary>
    public List<KeyValuePair<string, string>> Properties(object dlm, ObjectId id, int rowId, bool visibleOnly)
    {
        object? result = null;
        var byId = dlm.GetType().GetMethod("GetAllProperties", Instance, null, new[] { typeof(ObjectId), typeof(bool) }, null);
        if (byId is not null) result = Try(() => byId.Invoke(dlm, new object[] { id, visibleOnly }), "GetAllProperties");
        if (result is null)
        {
            var byRow = dlm.GetType().GetMethod("GetAllProperties", Instance, null, new[] { typeof(int), typeof(bool) }, null);
            if (byRow is not null) result = Try(() => byRow.Invoke(dlm, new object[] { rowId, visibleOnly }), "GetAllProperties");
        }
        return ToPairs(result);
    }

    /// <summary>Plant 3D class of the object (Pipe, Elbow, Valve, Equipment...), when the API says.</summary>
    public string? ClassName(object dlm, ObjectId id, int rowId)
    {
        foreach (var (name, arg) in new (string, object)[] { ("GetObjectClassname", id), ("GetObjectClassname", rowId), ("GetObjectClassName", id) })
        {
            var method = dlm.GetType().GetMethod(name, Instance, null, new[] { arg.GetType() }, null);
            if (method is null) continue;
            try
            {
                if (method.Invoke(dlm, new[] { arg }) is string s && s.Length > 0) return s;
            }
            catch { /* try the next form */ }
        }
        return null;
    }

    /// <summary>Sets Plant 3D properties of an object. Throws with Plant 3D's message when it refuses.</summary>
    public void SetProperties(object dlm, ObjectId id, int rowId, IReadOnlyList<string> names, IReadOnlyList<string> values)
    {
        foreach (var method in dlm.GetType().GetMethods(Instance).Where(m => m.Name == "SetProperties"))
        {
            var ps = method.GetParameters();
            if (ps.Length != 3) continue;
            object key;
            if (ps[0].ParameterType == typeof(ObjectId)) key = id;
            else if (ps[0].ParameterType == typeof(int)) key = rowId;
            else continue;
            var n = Collection(ps[1].ParameterType, names);
            var v = Collection(ps[2].ParameterType, values);
            if (n is null || v is null) continue;
            try
            {
                method.Invoke(dlm, new[] { key, n, v });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
            }
            return;
        }
        throw new NotSupportedException("This Plant 3D version has no DataLinksManager.SetProperties that Nexus can call.");
    }

    // ------------------------------------------------------------------ reflection helpers

    /// <summary>Simple public values of any object (strings, numbers, booleans, enums, dates, GUIDs), for listing.</summary>
    public static List<KeyValuePair<string, string>> Scalars(object o)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var p in o.GetType().GetProperties(Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.GetIndexParameters().Length > 0 || !IsScalar(p.PropertyType)) continue;
            try
            {
                var v = p.GetValue(o);
                list.Add(new(p.Name, v switch
                {
                    null => "",
                    IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
                    _ => v.ToString() ?? "",
                }));
            }
            catch { /* property not available in this state */ }
        }
        return list;
    }

    public static string? ScalarText(object o, string property)
    {
        try { return o.GetType().GetProperty(property, Instance)?.GetValue(o)?.ToString(); }
        catch { return null; }
    }

    private static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid);
    }

    private static object? Collection(Type type, IReadOnlyList<string> items)
    {
        if (type == typeof(StringCollection))
        {
            var c = new StringCollection();
            foreach (var s in items) c.Add(s);
            return c;
        }
        if (type == typeof(string[])) return items.ToArray();
        if (type.IsAssignableFrom(typeof(List<string>))) return items.ToList();
        return null;
    }

    private static List<KeyValuePair<string, string>> ToPairs(object? result)
    {
        if (result is IEnumerable<KeyValuePair<string, string>> typed) return typed.ToList();
        var list = new List<KeyValuePair<string, string>>();
        if (result is not IEnumerable items) return list;
        foreach (var item in items)
        {
            if (item is null) continue;
            string? key = ScalarText(item, "Key");
            if (key is not null) list.Add(new(key, ScalarText(item, "Value") ?? ""));
        }
        return list;
    }

    private static Type? FindType(string fullName, string assemblyName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(fullName, false);
            if (t is not null) return t;
        }
        try { return Assembly.Load(assemblyName).GetType(fullName, false); }
        catch { return null; }
    }

    private object? Try(Func<object?> get, string what)
    {
        try { return get(); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            Report(what, $"Plant 3D {what} failed: {inner.Message}");
            return null;
        }
    }

    /// <summary>Logs an API problem once per session.</summary>
    private void Report(string key, string message)
    {
        lock (_reported)
            if (!_reported.Add(key)) return;
        _log.Warn(message);
    }
}
