using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Nexus.Contracts;
using Autodesk.AutoCAD.DatabaseServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>
/// Reads the ActiveX/COM properties of an object (the ones the Properties palette
/// shows), with the palette's own category for each property when the object
/// implements ICategorizeProperties.
/// </summary>
public sealed class ComPropertyReader
{
    private const int LcidEnglish = 0x409;

    private static readonly HashSet<string> DeniedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application", "Document", "Database", "ObjectID", "ObjectID32", "OwnerID", "OwnerID32",
    };

    private static readonly Dictionary<int, string> StandardCategories = new()
    {
        [-1] = "Misc", [-2] = "Misc", [-3] = "Font", [-4] = "Position", [-5] = "Appearance",
        [-6] = "Behavior", [-7] = "Data", [-8] = "List", [-9] = "Text", [-10] = "Scale", [-11] = "DDE",
    };

    private sealed record ComProp(string Name, int DispId, bool CanWrite);

    private readonly Dictionary<string, List<ComProp>> _typeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, int), string?> _categoryCache = new();
    private readonly ProbeJournal _journal;

    public ComPropertyReader(ProbeJournal journal) => _journal = journal;

    public readonly record struct ComProperty(string Category, PropertyValue Value);

    /// <summary>Returns null when the object has no COM wrapper.</summary>
    public List<ComProperty>? Read(DBObject obj, Transaction? tr, string? editBlocker, out string? comTypeName)
    {
        comTypeName = null;
        object? com;
        try { com = obj.AcadObject; }
        catch { return null; }
        if (com is null || !Marshal.IsComObject(com)) return null;

        // Note: never Marshal.ReleaseComObject here. RCWs are shared process-wide and
        // releasing one could break another add-in holding the same object.
        var typeInfo = GetTypeInfo(com);
        if (typeInfo is null) return null;

        typeInfo.GetDocumentation(-1, out var typeName, out _, out _, out _);
        comTypeName = typeName;
        var props = GetProperties(typeName ?? "?", typeInfo);
        var categorizer = com as ICategorizeProperties;

        var result = new List<ComProperty>(props.Count);
        foreach (var p in props)
        {
            string key = $"C:{typeName}.{p.Name}";
            if (_journal.IsDenied(key)) continue;

            object? raw;
            _journal.Before(key);
            try
            {
                raw = com.GetType().InvokeMember(p.Name, BindingFlags.GetProperty, null, com, null, CultureInfo.InvariantCulture);
            }
            catch
            {
                _journal.After(key);
                continue; // not applicable to this object
            }
            _journal.After(key);

            var f = ValueFormatter.Format(raw, tr);
            result.Add(new ComProperty(Category(typeName ?? "?", p.DispId, categorizer) ?? "", new PropertyValue
            {
                Name = p.Name,
                Id = $"{typeName}.{p.Name} (dispid {p.DispId})",
                Source = PropertySource.Com,
                StorageType = f.StorageType,
                Value = f.Display,
                RawValue = f.Raw,
                HasValue = f.HasValue,
                IsReadOnly = !p.CanWrite || editBlocker is not null,
                ReadOnlyReason = !p.CanWrite ? "Read-only in the API" : editBlocker,
            }));
        }
        return result;
    }

    private static ComTypes.ITypeInfo? GetTypeInfo(object com)
    {
        try
        {
            if (com is not IDispatchInfo dispatch) return null;
            if (dispatch.GetTypeInfoCount(out uint count) != 0 || count == 0) return null;
            return dispatch.GetTypeInfo(0, LcidEnglish, out var ti) == 0 ? ti : null;
        }
        catch
        {
            return null;
        }
    }

    private string? Category(string typeName, int dispId, ICategorizeProperties? categorizer)
    {
        if (categorizer is null) return null;
        if (_categoryCache.TryGetValue((typeName, dispId), out var cached)) return cached;

        string? name = null;
        try
        {
            if (categorizer.MapPropertyToCategory(dispId, out int cat) == 0)
            {
                if (categorizer.GetCategoryName(cat, LcidEnglish, out var n) == 0 && !string.IsNullOrWhiteSpace(n))
                    name = n;
                else if (StandardCategories.TryGetValue(cat, out var std))
                    name = std;
            }
        }
        catch
        {
            // Categorization is best effort.
        }
        _categoryCache[(typeName, dispId)] = name;
        return name;
    }

    private List<ComProp> GetProperties(string typeName, ComTypes.ITypeInfo typeInfo)
    {
        if (_typeCache.TryGetValue(typeName, out var cached)) return cached;

        var getters = new Dictionary<int, string>();
        var setters = new HashSet<int>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Collect(typeInfo, getters, setters, visited);

        var list = getters
            .Where(g => !DeniedNames.Contains(g.Value))
            .Select(g => new ComProp(g.Value, g.Key, setters.Contains(g.Key)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _typeCache[typeName] = list;
        return list;
    }

    private static void Collect(ComTypes.ITypeInfo ti, Dictionary<int, string> getters, HashSet<int> setters, HashSet<string> visited)
    {
        ti.GetDocumentation(-1, out var name, out _, out _, out _);
        if (name is null || name is "IDispatch" or "IUnknown" || !visited.Add(name)) return;

        ti.GetTypeAttr(out IntPtr pAttr);
        ComTypes.TYPEATTR attr;
        try { attr = Marshal.PtrToStructure<ComTypes.TYPEATTR>(pAttr); }
        finally { ti.ReleaseTypeAttr(pAttr); }

        for (int i = 0; i < attr.cFuncs; i++)
        {
            ti.GetFuncDesc(i, out IntPtr pFunc);
            try
            {
                var fd = Marshal.PtrToStructure<ComTypes.FUNCDESC>(pFunc);
                const short Restricted = 0x1, Hidden = 0x40;
                if ((fd.wFuncFlags & (Restricted | Hidden)) != 0) continue;

                // Dispinterfaces fold [retval] into the return; dual vtable interfaces keep it as a parameter.
                int maxParams = attr.typekind == ComTypes.TYPEKIND.TKIND_DISPATCH ? 0 : 1;

                if (fd.invkind == ComTypes.INVOKEKIND.INVOKE_PROPERTYGET && fd.cParams <= maxParams)
                {
                    ti.GetDocumentation(fd.memid, out var propName, out _, out _, out _);
                    if (!string.IsNullOrEmpty(propName)) getters.TryAdd(fd.memid, propName);
                }
                else if (fd.invkind is ComTypes.INVOKEKIND.INVOKE_PROPERTYPUT or ComTypes.INVOKEKIND.INVOKE_PROPERTYPUTREF)
                {
                    setters.Add(fd.memid);
                }
            }
            finally
            {
                ti.ReleaseFuncDesc(pFunc);
            }
        }

        for (int j = 0; j < attr.cImplTypes; j++)
        {
            try
            {
                ti.GetRefTypeOfImplType(j, out int href);
                ti.GetRefTypeInfo(href, out var baseTi);
                Collect(baseTi, getters, setters, visited);
            }
            catch
            {
                // Skip unresolvable base interfaces.
            }
        }
    }
}
