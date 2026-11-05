using System.Reflection;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Civil3D;

/// <summary>
/// Some Civil 3D data is only available through parameterless Get...Properties()
/// methods (surface statistics etc.). Calls the known ones by reflection and
/// reads the returned object's properties.
/// </summary>
internal sealed class CivilMethodPropertiesExtender : IObjectPropertyExtender
{
    private static readonly (string Method, string Group)[] Methods =
    {
        ("GetGeneralProperties", "Statistics · General"),
        ("GetTinProperties", "Statistics · TIN"),
        ("GetTerrainProperties", "Statistics · Terrain"),
        ("GetGridProperties", "Statistics · Grid"),
    };

    private readonly ObjectPropertyReader _objects;

    public CivilMethodPropertiesExtender(ObjectPropertyReader objects) => _objects = objects;

    public bool AppliesTo(DBObject obj) => obj.GetType().Namespace?.StartsWith("Autodesk.Civil", StringComparison.Ordinal) == true;

    public void Extend(DBObject obj, Transaction tr, DataItem item, ObjectReadOptions options, string? editBlocker)
    {
        foreach (var (methodName, groupName) in Methods)
        {
            var method = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method is null || method.ReturnType == typeof(void)) continue;

            object? result;
            try { result = method.Invoke(obj, null); }
            catch { continue; } // e.g. not built yet / out of date

            if (result is null) continue;
            var group = new PropertyGroup(groupName);
            foreach (var mp in _objects.Managed.Read(result, tr, editBlocker))
            {
                mp.Value.IsReadOnly = true;
                mp.Value.ReadOnlyReason = "Computed statistic";
                mp.Value.Source = PropertySource.Derived;
                group.Properties.Add(mp.Value);
            }
            if (group.Properties.Count > 0) item.Groups.Add(group);
        }
    }
}
