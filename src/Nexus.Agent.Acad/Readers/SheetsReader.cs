using System.Collections;
using System.Globalization;
using Nexus.Agent;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Readers;

/// <summary>
/// One row per layout (sheet): layout and page setup, the title block's attributes,
/// and the drawing's properties (DWGPROPS). The sheet-index counterpart of Revit's sheets.
/// Title block attributes and DWGPROPS are editable from the hub.
/// </summary>
internal sealed class SheetsReader : IHostDataReader<Document>
{
    public const string LayoutNameId = "Layout:Name";
    public const string DwgPropsPrefix = "DwgProps:";
    public const string DwgPropsCustomPrefix = "DwgProps:Custom:";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] TitleBlockHints = { "TITLE", "TTLB", "TB", "BORDER", "SHEET", "FRAME" };

    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "acad.sheets",
        DisplayName = "Sheets (Layouts + Title Block)",
        Domain = "Sheets",
        Description = "One row per layout: layout and page setup, every title block attribute, and the drawing properties (DWGPROPS). Use Layouts for every paper space object.",
        Options =
        {
            new ReaderOption
            {
                Name = "titleBlockNames", DisplayName = "Title block names (comma separated, * wildcards)", Type = "string", Default = "",
                Description = "Empty: the block whose name looks like a title block (TITLE, TB, BORDER, SHEET...), else the one with the most attributes.",
            },
            ReaderOption.Bool("drawingProperties", "Drawing properties (DWGPROPS)", true),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        string? docBlocker = doc.IsReadOnly ? "Drawing is read-only" : null;
        string docId = AcadDocumentProvider.Id(doc);
        var patterns = (ctx.GetString("titleBlockNames") ?? "")
            .Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        using var docLock = AcadDocumentProvider.LockForRead(doc);
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();

        var drawingProps = ctx.GetBool("drawingProperties") ? DrawingProperties(db, docId, docBlocker) : null;

        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var layouts = new List<Layout>();
        foreach (DBDictionaryEntry entry in dict)
            if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout l && !l.ModelType)
                layouts.Add(l);

        foreach (var layout in layouts.OrderBy(l => l.TabOrder))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            try
            {
                ctx.Items.Add(ReadLayout(doc, layout, tr, patterns, docBlocker, drawingProps, ctx));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ctx.Warn($"Layout '{SafeName(layout)}': {ex.Message}");
                ctx.Log.Warn($"Layout '{SafeName(layout)}' failed.", ex);
            }
        }
        tr.Commit();
    }

    private static DataItem ReadLayout(Document doc, Layout layout, Transaction tr, string[] patterns,
        string? docBlocker, PropertyGroup? drawingProps, ReadContext ctx)
    {
        string handle = layout.ObjectId.Handle.ToString();
        var item = new DataItem
        {
            Id = handle,
            ItemType = "Layout",
            Name = layout.LayoutName,
            Key = layout.LayoutName,
        };

        var g = new PropertyGroup("Layout");
        g.Properties.Add(new PropertyValue
        {
            Name = "Layout Name", Id = LayoutNameId, OwnerId = handle, Source = PropertySource.Managed,
            StorageType = "String", Value = layout.LayoutName, RawValue = layout.LayoutName,
            IsReadOnly = docBlocker is not null, ReadOnlyReason = docBlocker,
        });
        g.Properties.Add(ObjectPropertyReader.Derived("Tab Order", layout.TabOrder.ToString(Inv)));
        g.Properties.Add(ObjectPropertyReader.Derived("Drawing", Path.GetFileName(doc.Name)));
        g.Properties.Add(ObjectPropertyReader.Derived("Page Setup", Safe(() => layout.PlotSettingsName)));
        g.Properties.Add(ObjectPropertyReader.Derived("Plotter", Safe(() => layout.PlotConfigurationName)));
        g.Properties.Add(ObjectPropertyReader.Derived("Paper Size", Safe(() => layout.CanonicalMediaName)));
        g.Properties.Add(ObjectPropertyReader.Derived("Plot Scale", Safe(() => PlotScale(layout))));
        g.Properties.Add(ObjectPropertyReader.Derived("Plot Style Table", Safe(() => layout.CurrentStyleSheet)));

        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var candidates = new List<(BlockReference Block, string Name, List<AttributeReference> Attributes)>();
        var viewportScales = new List<string>();
        foreach (ObjectId id in btr)
        {
            var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
            if (obj is Viewport vp && vp.Number != 1 && vp.CustomScale > 0)
                viewportScales.Add("1:" + (1.0 / vp.CustomScale).ToString("0.##", Inv));
            if (obj is not BlockReference br || br.AttributeCollection.Count == 0) continue;
            var attrs = new List<AttributeReference>();
            foreach (ObjectId aid in br.AttributeCollection)
                if (tr.GetObject(aid, OpenMode.ForRead, false, true) is AttributeReference ar) attrs.Add(ar);
            if (attrs.Count > 0) candidates.Add((br, ObjectPropertyReader.EffectiveName(br, tr), attrs));
        }
        g.Properties.Add(ObjectPropertyReader.Derived("Viewport Scales", string.Join(", ", viewportScales.Distinct())));
        item.Groups.Add(g);

        var titleBlock = PickTitleBlock(candidates, patterns);
        var tb = new PropertyGroup("Title Block");
        if (titleBlock is { } chosen)
        {
            tb.Properties.Add(ObjectPropertyReader.Derived("Title Block Name", chosen.Name));
            string? blockBlocker = docBlocker; // locked layers are unlocked briefly when applying
            foreach (var ar in chosen.Attributes)
            {
                string value = ar.IsMTextAttribute ? ar.MTextAttribute?.Contents ?? ar.TextString : ar.TextString;
                string? reason = ar.IsConstant ? "Constant attribute" : blockBlocker;
                string ah = ar.ObjectId.Handle.ToString();
                tb.Properties.Add(new PropertyValue
                {
                    Name = ar.Tag, Id = ah, OwnerId = ah, Source = PropertySource.Attribute,
                    StorageType = "String", DataType = ar.IsMTextAttribute ? "Multiline attribute" : "Attribute",
                    Value = value, RawValue = value, IsReadOnly = reason is not null, ReadOnlyReason = reason,
                });
            }
            if (candidates.Count > 1)
                tb.Properties.Add(ObjectPropertyReader.Derived("Other Attributed Blocks",
                    string.Join(", ", candidates.Where(c => c.Block != chosen.Block).Select(c => c.Name).Distinct())));
        }
        else
        {
            tb.Properties.Add(ObjectPropertyReader.Derived("Title Block Name", ""));
            ctx.Warn($"Layout '{layout.LayoutName}': no title block with attributes was found.");
        }
        item.Groups.Add(tb);

        if (drawingProps is not null) item.Groups.Add(drawingProps);
        return item;
    }

    private static (BlockReference Block, string Name, List<AttributeReference> Attributes)? PickTitleBlock(
        List<(BlockReference Block, string Name, List<AttributeReference> Attributes)> candidates, string[] patterns)
    {
        if (candidates.Count == 0) return null;
        if (patterns.Length > 0)
        {
            var named = candidates.Where(c => patterns.Any(p => Wildcard(c.Name, p))).ToList();
            return named.Count == 0 ? null : named.OrderByDescending(c => c.Attributes.Count).First();
        }
        return candidates
            .OrderByDescending(c => TitleBlockHints.Any(h => c.Name.Contains(h, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenByDescending(c => c.Attributes.Count)
            .First();
    }

    private static bool Wildcard(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(text,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>DWGPROPS: summary fields and custom properties. Shared by every layout row of the drawing.</summary>
    private static PropertyGroup DrawingProperties(Database db, string docId, string? docBlocker)
    {
        var g = new PropertyGroup("Drawing Properties");
        var info = db.SummaryInfo;
        void Add(string name, string? value, bool editable = true) => g.Properties.Add(new PropertyValue
        {
            Name = name, Id = DwgPropsPrefix + name.Replace(" ", ""), OwnerId = docId, Source = PropertySource.Setting,
            StorageType = "String", Value = value ?? "", RawValue = value ?? "",
            IsReadOnly = !editable || docBlocker is not null,
            ReadOnlyReason = !editable ? "Set by AutoCAD" : docBlocker,
        });
        Add("Title", info.Title);
        Add("Subject", info.Subject);
        Add("Author", info.Author);
        Add("Keywords", info.Keywords);
        Add("Comments", info.Comments);
        Add("Hyperlink Base", info.HyperlinkBase);
        Add("Revision Number", info.RevisionNumber);
        Add("Last Saved By", info.LastSavedBy, editable: false);

        var custom = info.CustomProperties;
        while (custom.MoveNext())
        {
            var entry = (DictionaryEntry)custom.Current;
            string key = Convert.ToString(entry.Key, Inv) ?? "";
            string value = Convert.ToString(entry.Value, Inv) ?? "";
            g.Properties.Add(new PropertyValue
            {
                Name = key, Id = DwgPropsCustomPrefix + key, OwnerId = docId, Source = PropertySource.Setting,
                StorageType = "String", DataType = "Custom property", Value = value, RawValue = value,
                IsReadOnly = docBlocker is not null, ReadOnlyReason = docBlocker,
            });
        }
        return g;
    }

    /// <summary>Current DWGPROPS value for a property id, as read above.</summary>
    public static string? CurrentDwgProp(Database db, string id)
    {
        var info = db.SummaryInfo;
        if (id.StartsWith(DwgPropsCustomPrefix, StringComparison.Ordinal))
        {
            string key = id[DwgPropsCustomPrefix.Length..];
            var custom = info.CustomProperties;
            while (custom.MoveNext())
            {
                var entry = (DictionaryEntry)custom.Current;
                if (string.Equals(Convert.ToString(entry.Key, Inv), key, StringComparison.Ordinal))
                    return Convert.ToString(entry.Value, Inv) ?? "";
            }
            return "";
        }
        return id[DwgPropsPrefix.Length..] switch
        {
            "Title" => info.Title, "Subject" => info.Subject, "Author" => info.Author, "Keywords" => info.Keywords,
            "Comments" => info.Comments, "HyperlinkBase" => info.HyperlinkBase, "RevisionNumber" => info.RevisionNumber,
            _ => null,
        } ?? "";
    }

    /// <summary>Sets one DWGPROPS value (summary field or custom property).</summary>
    public static void SetDwgProp(Database db, string id, string value)
    {
        var builder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
        if (id.StartsWith(DwgPropsCustomPrefix, StringComparison.Ordinal))
        {
            builder.CustomPropertyTable[id[DwgPropsCustomPrefix.Length..]] = value;
        }
        else
        {
            switch (id[DwgPropsPrefix.Length..])
            {
                case "Title": builder.Title = value; break;
                case "Subject": builder.Subject = value; break;
                case "Author": builder.Author = value; break;
                case "Keywords": builder.Keywords = value; break;
                case "Comments": builder.Comments = value; break;
                case "HyperlinkBase": builder.HyperlinkBase = value; break;
                case "RevisionNumber": builder.RevisionNumber = value; break;
                default: throw new FormatException("This drawing property cannot be changed.");
            }
        }
        db.SummaryInfo = builder.ToDatabaseSummaryInfo();
    }

    private static string PlotScale(Layout layout)
    {
        if (layout.UseStandardScale) return layout.StdScaleType.ToString();
        var s = layout.CustomPrintScale;
        return s.Denominator == 0 ? "" : $"{s.Numerator.ToString("0.###", Inv)}:{s.Denominator.ToString("0.###", Inv)}";
    }

    private static string SafeName(Layout l)
    {
        try { return l.LayoutName; } catch { return "?"; }
    }

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }
}
