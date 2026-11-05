using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>Turns arbitrary API values (managed or COM) into display + raw strings.</summary>
public static class ValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> NameProps = new();

    public readonly record struct Formatted(string? Display, string? Raw, string StorageType, bool HasValue);

    public static Formatted Format(object? value, Transaction? tr)
    {
        switch (value)
        {
            case null:
                return new(null, null, "Null", false);
            case string s:
                return new(s, s, "String", true);
            case bool b:
                return new(b ? "Yes" : "No", b ? "True" : "False", "Boolean", true);
            case Enum e:
                return new(e.ToString(), Convert.ToInt64(e, Inv).ToString(Inv), "Enum:" + e.GetType().Name, true);
            case double d:
                return new(d.ToString("0.########", Inv), d.ToString("R", Inv), "Double", true);
            case float f:
                return new(f.ToString("0.######", Inv), f.ToString("R", Inv), "Single", true);
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                var n = Convert.ToString(value, Inv);
                return new(n, n, value.GetType().Name, true);
            case ObjectId id:
                return new(DescribeId(id, tr), id.IsNull ? "" : id.Handle.ToString(), "ObjectId", !id.IsNull);
            case Handle h:
                return new(h.ToString(), h.ToString(), "Handle", true);
            case Point3d p3:
                return new(P(p3.X, p3.Y, p3.Z), P(p3.X, p3.Y, p3.Z), "Point3d", true);
            case Point2d p2:
                return new(P(p2.X, p2.Y), P(p2.X, p2.Y), "Point2d", true);
            case Vector3d v3:
                return new(P(v3.X, v3.Y, v3.Z), P(v3.X, v3.Y, v3.Z), "Vector3d", true);
            case Vector2d v2:
                return new(P(v2.X, v2.Y), P(v2.X, v2.Y), "Vector2d", true);
            case Scale3d sc:
                return new(P(sc.X, sc.Y, sc.Z), P(sc.X, sc.Y, sc.Z), "Scale3d", true);
            case Extents3d ex:
                var es = $"({P(ex.MinPoint.X, ex.MinPoint.Y, ex.MinPoint.Z)}) - ({P(ex.MaxPoint.X, ex.MaxPoint.Y, ex.MaxPoint.Z)})";
                return new(es, es, "Extents3d", true);
            case Autodesk.AutoCAD.Colors.Color c:
                return new(c.ColorNameForDisplay, c.ColorValue.ToArgb().ToString("X8", Inv), "Color", true);
            case double[] arr when arr.Length <= 16:
                var joined = P(arr);
                return new(joined, joined, "Double[]", true);
            case DBObject dbo:
                try
                {
                    var name = TryName(dbo) ?? dbo.GetType().Name;
                    return new(name, dbo.ObjectId.IsNull ? name : dbo.ObjectId.Handle.ToString(), dbo.GetType().Name, true);
                }
                finally
                {
                    // Getters sometimes return new, non-database objects; release their native memory.
                    if (dbo.ObjectId.IsNull && !dbo.IsDisposed) dbo.Dispose();
                }
        }

        if (Marshal.IsComObject(value))
        {
            var com = DescribeCom(value);
            return new(com, com, "COM", true);
        }

        if (value is ICollection col)
            return new($"[{col.Count} items]", col.Count.ToString(Inv), "Collection", true);

        // Unknown struct/class: prefer a Name property, else ToString.
        var named = TryName(value);
        if (named is not null) return new(named, named, value.GetType().Name, true);

        string text;
        try { text = Convert.ToString(value, Inv) ?? ""; }
        catch { text = ""; }
        if (text == value.GetType().FullName || text == value.GetType().ToString()) text = $"({value.GetType().Name})";
        return new(text, text, value.GetType().Name, true);
    }

    public static string DescribeId(ObjectId id, Transaction? tr)
    {
        if (id.IsNull) return "";
        if (id.IsErased) return $"<erased {id.Handle}>";
        if (tr is null) return id.Handle.ToString();
        try
        {
            var o = tr.GetObject(id, OpenMode.ForRead, false, true);
            if (o is SymbolTableRecord str) return str.Name;
            return TryName(o) ?? $"{o.GetType().Name} ({id.Handle})";
        }
        catch
        {
            return id.Handle.ToString();
        }
    }

    public static string? TryName(object o)
    {
        var prop = NameProps.GetOrAdd(o.GetType(), t =>
        {
            var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            return p is not null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 ? p : null;
        });
        if (prop is null) return null;
        try { return prop.GetValue(o) as string; }
        catch { return null; }
    }

    private static readonly string[] ComNameProbes = { "Name", "ColorName", "Count", "ObjectName" };

    public static string DescribeCom(object com)
    {
        foreach (var probe in ComNameProbes)
        {
            try
            {
                var v = com.GetType().InvokeMember(probe, BindingFlags.GetProperty, null, com, null, Inv);
                if (v is null) continue;
                var s = Convert.ToString(v, Inv);
                if (string.IsNullOrEmpty(s)) continue;
                return probe == "Count" ? $"[{s} items]" : s;
            }
            catch
            {
                // Try the next probe.
            }
        }
        return "(COM object)";
    }

    private static string P(params double[] values) =>
        string.Join(", ", values.Select(v => v.ToString("0.########", Inv)));
}
