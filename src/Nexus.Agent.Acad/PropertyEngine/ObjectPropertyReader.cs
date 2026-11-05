using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.PropertyEngine;

public sealed class ObjectReadOptions
{
    public bool Com { get; set; } = true;
    public bool Managed { get; set; } = true;
    public bool Attributes { get; set; } = true;
    public bool TableCells { get; set; } = true;
    public int MaxTableCells { get; set; } = 2000;
}

/// <summary>
/// Adds host- or vertical-specific extras to an object's properties
/// (e.g. Civil 3D surface statistics). Registered by modules.
/// </summary>
public interface IObjectPropertyExtender
{
    bool AppliesTo(DBObject obj);
    void Extend(DBObject obj, Transaction tr, DataItem item, ObjectReadOptions options, string? editBlocker);
}

/// <summary>
/// Generic "Properties palette" reader for any AutoCAD database object:
/// COM properties grouped by palette category, then managed-API properties the
/// COM layer does not cover, plus block attributes, dynamic block properties,
/// viewport layer state and table cells.
/// </summary>
public sealed class ObjectPropertyReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ManagedPropertyReader _managed;
    private readonly ComPropertyReader _com;

    public ObjectPropertyReader(ProbeJournal journal, CategoryMap categories, AgentLog log)
    {
        Journal = journal;
        Categories = categories;
        Log = log;
        _managed = new ManagedPropertyReader(journal);
        _com = new ComPropertyReader(journal);
    }

    public ProbeJournal Journal { get; }
    public CategoryMap Categories { get; }
    public AgentLog Log { get; }
    public ManagedPropertyReader Managed => _managed;
    public List<IObjectPropertyExtender> Extenders { get; } = new();

    public DataItem Read(DBObject obj, Transaction tr, ObjectReadOptions options, string? documentBlocker)
    {
        // Locked layers do not block edits: the writer unlocks the layer for the change and locks it again.
        string? blocker = documentBlocker;
        var item = new DataItem
        {
            Id = obj.ObjectId.IsNull ? "" : obj.ObjectId.Handle.ToString(),
            ItemType = obj.GetType().Name,
            Name = Describe(obj, tr),
            Key = KeyOf(obj, tr),
        };

        var info = new PropertyGroup("Object");
        info.Properties.Add(Derived("Handle", item.Id));
        info.Properties.Add(Derived(".NET Type", obj.GetType().FullName));
        try { info.Properties.Add(Derived("DXF Name", obj.GetRXClass()?.DxfName)); } catch { /* ignored */ }
        info.Properties.Add(Derived("Editable", blocker is null ? "Yes" : "No: " + blocker));
        item.Groups.Add(info);

        var groups = new Dictionary<string, PropertyGroup>(StringComparer.Ordinal);
        PropertyGroup GroupFor(string name)
        {
            if (!groups.TryGetValue(name, out var g))
            {
                g = new PropertyGroup(name);
                groups[name] = g;
                item.Groups.Add(g);
            }
            return g;
        }

        var comNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Com)
        {
            try
            {
                var comProps = _com.Read(obj, tr, blocker, out _);
                if (comProps is not null)
                {
                    foreach (var cp in comProps)
                    {
                        string cat = cp.Category.Length > 0 ? cp.Category : Categories.CategoryFor(cp.Value.Name) ?? "Misc";
                        GroupFor(cat).Properties.Add(cp.Value);
                        comNames.Add(cp.Value.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"COM properties failed for {item.ItemType} {item.Id}.", ex);
            }
        }

        if (options.Managed)
        {
            try
            {
                foreach (var mp in _managed.Read(obj, tr, blocker))
                {
                    if (comNames.Contains(mp.Value.Name)) continue; // the COM layer already shows it
                    string cat = (comNames.Count == 0 ? Categories.CategoryFor(mp.Value.Name) : null)
                                 ?? $".NET · {mp.Info.DeclaringType?.Name}";
                    GroupFor(cat).Properties.Add(mp.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Managed properties failed for {item.ItemType} {item.Id}.", ex);
            }
        }

        TryExtra(item, () => AddSpecifics(obj, tr, item, options, blocker));

        foreach (var ext in Extenders)
        {
            TryExtra(item, () =>
            {
                if (ext.AppliesTo(obj)) ext.Extend(obj, tr, item, options, blocker);
            });
        }

        // Edits are sent to the object that owns each property (this one unless set, e.g. attributes).
        foreach (var g in item.Groups)
            foreach (var p in g.Properties)
                if (p.OwnerId is null && p.Source != PropertySource.Derived) p.OwnerId = item.Id;
        return item;
    }

    private void TryExtra(DataItem item, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn($"Extra properties failed for {item.ItemType} {item.Id}.", ex); }
    }

    private void AddSpecifics(DBObject obj, Transaction tr, DataItem item, ObjectReadOptions options, string? blocker)
    {
        switch (obj)
        {
            // Table derives from BlockReference, so it must come first.
            case Table table:
                if (options.TableCells) AddTable(table, item, options, blocker);
                break;
            case BlockReference br:
                AddBlock(br, tr, item, options, blocker);
                break;
            case Viewport vp:
                AddViewport(vp, tr, item);
                break;
        }
    }

    private static void AddBlock(BlockReference br, Transaction tr, DataItem item, ObjectReadOptions options, string? blocker)
    {
        var block = new PropertyGroup("Block");
        block.Properties.Add(Derived("Effective Name", EffectiveName(br, tr)));
        block.Properties.Add(Derived("Is Dynamic", br.IsDynamicBlock ? "Yes" : "No"));
        item.Groups.Add(block);

        if (options.Attributes && br.AttributeCollection.Count > 0)
        {
            var attrs = new PropertyGroup("Attributes");
            foreach (ObjectId id in br.AttributeCollection)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false, true) is not AttributeReference ar) continue;
                string value = ar.IsMTextAttribute ? ar.MTextAttribute?.Contents ?? ar.TextString : ar.TextString;
                string? reason = ar.IsConstant ? "Constant attribute" : blocker;
                attrs.Properties.Add(new PropertyValue
                {
                    Name = ar.Tag,
                    Id = ar.ObjectId.Handle.ToString(),
                    OwnerId = ar.ObjectId.Handle.ToString(),
                    Source = PropertySource.Attribute,
                    StorageType = "String",
                    DataType = ar.IsMTextAttribute ? "Multiline attribute" : "Attribute",
                    Value = value,
                    RawValue = value,
                    IsReadOnly = reason is not null,
                    ReadOnlyReason = reason,
                });
            }
            item.Groups.Add(attrs);
        }

        if (br.IsDynamicBlock)
        {
            var custom = new PropertyGroup("Custom");
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                if (p.PropertyName == "Origin") continue; // the palette hides it too
                var f = ValueFormatter.Format(p.Value, tr);
                custom.Properties.Add(new PropertyValue
                {
                    Name = p.PropertyName,
                    Id = "Dyn:" + p.PropertyName,
                    Source = PropertySource.DynamicBlock,
                    StorageType = f.StorageType,
                    DataType = p.UnitsType.ToString(),
                    Value = f.Display,
                    RawValue = f.Raw,
                    HasValue = f.HasValue,
                    IsReadOnly = p.ReadOnly || blocker is not null,
                    ReadOnlyReason = p.ReadOnly ? "Read-only dynamic property" : blocker,
                });
            }
            if (custom.Properties.Count > 0) item.Groups.Add(custom);
        }
    }

    private static void AddViewport(Viewport vp, Transaction tr, DataItem item)
    {
        var g = new PropertyGroup("Viewport");
        double scale = vp.CustomScale;
        g.Properties.Add(Derived("Scale (1:n)", scale > 0 ? (1.0 / scale).ToString("0.####", Inv) : ""));
        g.Properties.Add(Derived("Scale (paper:model)", scale.ToString("0.########", Inv)));

        var frozen = new List<string>();
        try
        {
            var ids = vp.GetFrozenLayers();
            if (ids is not null)
                foreach (ObjectId id in ids)
                    frozen.Add(ValueFormatter.DescribeId(id, tr));
        }
        catch { /* not initialized */ }
        g.Properties.Add(Derived("VP Frozen Layers", string.Join("; ", frozen)));
        g.Properties.Add(Derived("VP Frozen Layer Count", frozen.Count.ToString(Inv)));
        item.Groups.Add(g);
    }

    private static void AddTable(Table table, DataItem item, ObjectReadOptions options, string? blocker)
    {
        var g = new PropertyGroup("Cells");
        int rows = table.Rows.Count, cols = table.Columns.Count, n = 0;
        g.Properties.Add(Derived("Rows", rows.ToString(Inv)));
        g.Properties.Add(Derived("Columns", cols.ToString(Inv)));
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (++n > options.MaxTableCells)
                {
                    g.Properties.Add(Derived("(truncated)", $"More than {options.MaxTableCells} cells"));
                    item.Groups.Add(g);
                    return;
                }
                string text;
                try { text = table.Cells[r, c].TextString ?? ""; }
                catch { continue; }
                g.Properties.Add(new PropertyValue
                {
                    Name = $"R{r + 1}C{c + 1}",
                    Id = TableCellId(r, c),
                    Source = PropertySource.Managed,
                    StorageType = "String",
                    Value = text,
                    RawValue = text,
                    IsReadOnly = blocker is not null,
                    ReadOnlyReason = blocker,
                });
            }
        }
        item.Groups.Add(g);
    }

    /// <summary>Property id of a table cell (zero-based row and column).</summary>
    public static string TableCellId(int row, int column) => FormattableString.Invariant($"Cell:{row},{column}");

    public static string EffectiveName(BlockReference br, Transaction tr)
    {
        try
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        catch
        {
            return br.Name;
        }
    }

    private static string Describe(DBObject obj, Transaction tr)
    {
        try
        {
            switch (obj)
            {
                case Table t: return $"Table ({t.Rows.Count}x{t.Columns.Count})";
                case BlockReference br: return $"Block: {EffectiveName(br, tr)}";
                case MText mt: return "MText: " + Trim(mt.Text);
                case DBText t: return "Text: " + Trim(t.TextString);
                case Viewport vp: return vp.CustomScale > 0 ? $"Viewport {vp.Number} (1:{1.0 / vp.CustomScale:0.##})" : $"Viewport {vp.Number}";
                case Layout l: return l.LayoutName;
            }
            var name = ValueFormatter.TryName(obj);
            return string.IsNullOrWhiteSpace(name) ? $"{obj.GetType().Name} {obj.ObjectId.Handle}" : $"{obj.GetType().Name}: {name}";
        }
        catch
        {
            return obj.GetType().Name;
        }
    }

    private static string? KeyOf(DBObject obj, Transaction tr) => obj switch
    {
        Table => null,
        BlockReference br => EffectiveName(br, tr),
        Layout l => l.LayoutName,
        _ => null,
    };

    private static string Trim(string? s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    public static PropertyValue Derived(string name, string? value) => new()
    {
        Name = name,
        Value = value,
        RawValue = value,
        Source = PropertySource.Derived,
        StorageType = "String",
        IsReadOnly = true,
        ReadOnlyReason = "Computed by Nexus",
    };
}
