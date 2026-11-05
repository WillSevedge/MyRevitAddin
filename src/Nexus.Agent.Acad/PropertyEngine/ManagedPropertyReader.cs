using System.Collections.Concurrent;
using System.Reflection;
using Nexus.Contracts;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>Reads public instance properties of a managed API object via reflection.</summary>
public sealed class ManagedPropertyReader
{
    /// <summary>Plumbing properties that are never useful to a user (or are unsafe to read).</summary>
    private static readonly HashSet<string> DeniedNames = new(StringComparer.Ordinal)
    {
        "AcadObject", "UnmanagedObject", "AutoDelete", "IsDisposed", "Database", "Drawable", "DrawableType",
        "ClassID", "XData", "Id", "ObjectId", "IsTransactionResident", "IsReallyClosing", "IsNewObject",
        "IsNotifyEnabled", "IsWriteEnabled", "IsReadEnabled", "IsNotifying", "IsUndoing", "IsModified",
        "IsModifiedXData", "IsModifiedGraphics", "IsCancelling", "IsAProxy", "IsEraseStatusToggled",
        "HasSaveVersionOverride", "ObjectBirthVersion", "IsObjectIdsInFlux", "CollisionType", "IsPersistent",
        "IsErased", "Bounds", "CompoundObjectTransform", "Ecs", "BlockName", "UndoFiler", "OwnerId",
        // Allocate GDI bitmaps on every call.
        "Thumbnail", "PreviewIcon", "ThumbnailBitmap",
    };

    private readonly ConcurrentDictionary<Type, PropertyInfo[]> _cache = new();
    private readonly ProbeJournal _journal;

    public ManagedPropertyReader(ProbeJournal journal) => _journal = journal;

    public readonly record struct ManagedProperty(PropertyInfo Info, PropertyValue Value);

    public List<ManagedProperty> Read(object obj, Transaction? tr, string? editBlocker)
    {
        var result = new List<ManagedProperty>();
        foreach (var prop in PropertiesOf(obj.GetType()))
        {
            string key = $"M:{prop.DeclaringType?.FullName}.{prop.Name}";
            if (_journal.IsDenied(key)) continue;

            object? raw;
            _journal.Before(key);
            try
            {
                raw = prop.GetValue(obj);
            }
            catch
            {
                // Not applicable to this object (the API throws eNotApplicable etc.).
                _journal.After(key);
                continue;
            }
            _journal.After(key);

            var f = ValueFormatter.Format(raw, tr);
            bool canWrite = prop.SetMethod is { IsPublic: true };
            var pv = new PropertyValue
            {
                Name = prop.Name,
                Id = key[2..],
                Source = PropertySource.Managed,
                StorageType = f.StorageType,
                DataType = prop.PropertyType.Name,
                Value = f.Display,
                RawValue = f.Raw,
                HasValue = f.HasValue,
                IsReadOnly = !canWrite || editBlocker is not null,
                ReadOnlyReason = !canWrite ? "Read-only in the API" : editBlocker,
            };
            result.Add(new ManagedProperty(prop, pv));
        }
        return result;
    }

    /// <summary>Read only the named properties (in the given order) of an object.</summary>
    public List<PropertyValue> ReadNamed(object obj, Transaction? tr, IEnumerable<string> names, string? editBlocker)
    {
        var all = PropertiesOf(obj.GetType()).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var list = new List<PropertyValue>();
        foreach (var name in names)
        {
            if (!all.TryGetValue(name, out var prop)) continue;
            string key = $"M:{prop.DeclaringType?.FullName}.{prop.Name}";
            if (_journal.IsDenied(key)) continue;
            object? raw;
            _journal.Before(key);
            try { raw = prop.GetValue(obj); }
            catch { _journal.After(key); continue; }
            _journal.After(key);

            var f = ValueFormatter.Format(raw, tr);
            bool canWrite = prop.SetMethod is { IsPublic: true };
            list.Add(new PropertyValue
            {
                Name = prop.Name,
                Id = key[2..],
                Source = PropertySource.Managed,
                StorageType = f.StorageType,
                DataType = prop.PropertyType.Name,
                Value = f.Display,
                RawValue = f.Raw,
                HasValue = f.HasValue,
                IsReadOnly = !canWrite || editBlocker is not null,
                ReadOnlyReason = !canWrite ? "Read-only in the API" : editBlocker,
            });
        }
        return list;
    }

    private PropertyInfo[] PropertiesOf(Type type) => _cache.GetOrAdd(type, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Where(p => !DeniedNames.Contains(p.Name))
            .Where(p => p.PropertyType != typeof(IntPtr) && !typeof(Delegate).IsAssignableFrom(p.PropertyType)
                        && p.PropertyType != typeof(Database))
            .GroupBy(p => p.Name).Select(g => g.First()) // hide 'new' re-declarations
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray());
}
