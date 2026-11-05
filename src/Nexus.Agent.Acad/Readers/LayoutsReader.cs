using System.Globalization;
using Nexus.Agent;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nexus.Agent.Acad.Readers;

/// <summary>
/// Every layout (sheet) in a drawing: layout-level properties (what the Properties
/// palette shows with nothing selected), every paper space object with all of its
/// properties, and optionally the model space objects visible through each viewport.
/// </summary>
internal sealed class LayoutsReader : IHostDataReader<Document>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Drawing-level current settings shown in the palette with nothing selected.</summary>
    private static readonly string[] DatabaseSettings =
    {
        "Cecolor", "Clayer", "Celtype", "Celtscale", "Celweight", "Cetransparency", "Cannoscale",
        "Ltscale", "Psltscale", "Msltscale", "Insunits", "Lunits", "Luprec", "Aunits", "Auprec", "Measurement",
        "Textstyle", "Dimstyle", "Tilemode", "Pucsname", "Pucsorg", "Pucsxdir", "Pucsydir",
        "Ucsname", "Ucsorg", "Ucsxdir", "Ucsydir", "Pextmin", "Pextmax", "Plimmin", "Plimmax",
        "Extmin", "Extmax", "Limmin", "Limmax", "PlotStyleMode", "OriginalFileVersion", "LastSavedAsVersion",
    };

    /// <summary>System variables (active document only) that the palette shows with nothing selected.</summary>
    private static readonly (string Name, bool ReadOnly)[] SystemVariables =
    {
        ("CTAB", false), ("CVPORT", false), ("VIEWCTR", true), ("VIEWSIZE", true), ("SCREENSIZE", true),
        ("VIEWTWIST", true), ("CANNOSCALE", false), ("ANNOALLVISIBLE", false), ("ANNOAUTOSCALE", false),
        ("UCSICON", false), ("UCSVP", false), ("UCSNAME", true), ("UCSORG", true), ("UCSFOLLOW", false),
        ("CPLOTSTYLE", false), ("PSTYLEMODE", true), ("LWDISPLAY", false), ("TRANSPARENCYDISPLAY", false),
        ("VSCURRENT", false), ("DEFLPLSTYLE", false),
    };

    /// <summary>Overall paper space viewport properties = the layout's "View" in the palette.</summary>
    private static readonly string[] PaperViewProps =
    {
        "ViewCenter", "ViewHeight", "Width", "Height", "CenterPoint", "VisualStyleId",
        "UcsPerViewport", "UcsIconVisible", "UcsIconAtOrigin", "UcsName", "AnnotationScale",
    };

    private readonly ObjectPropertyReader _objects;

    public LayoutsReader(ObjectPropertyReader objects) => _objects = objects;

    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "acad.layouts",
        DisplayName = "Layouts (Sheets)",
        Domain = "Layouts",
        Description = "Every layout with its page setup/plot/view settings, every paper space object with all properties, and optionally model space objects visible through viewports.",
        Options =
        {
            ReaderOption.Bool("paperSpaceObjects", "Paper space objects", true),
            ReaderOption.Bool("modelThroughViewports", "Model space objects visible in viewports", false,
                "Plan viewports only; uses the rectangular viewport extents."),
            ReaderOption.Int("maxModelObjectsPerViewport", "Max model objects per viewport", 500),
            ReaderOption.Int("maxPaperSpaceObjects", "Max paper space objects per layout", 5000),
            ReaderOption.Bool("comProperties", "COM/ActiveX properties (palette categories)", true),
            ReaderOption.Bool("managedProperties", ".NET API properties", true),
            ReaderOption.Bool("attributes", "Block attributes", true),
            ReaderOption.Bool("tableCells", "Table cell text", true),
            ReaderOption.Bool("drawingSettings", "Drawing settings and system variables", true),
            ReaderOption.Bool("modelLayout", "Include the Model layout", false),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var options = new ObjectReadOptions
        {
            Com = ctx.GetBool("comProperties"),
            Managed = ctx.GetBool("managedProperties"),
            Attributes = ctx.GetBool("attributes"),
            TableCells = ctx.GetBool("tableCells"),
        };
        string? docBlocker = doc.IsReadOnly ? "Drawing is read-only" : null;
        bool isActive = ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument);

        using var docLock = AcadDocumentProvider.LockForRead(doc);
        var db = doc.Database;
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();

            if (ctx.GetBool("drawingSettings"))
                ctx.Items.Add(ReadDrawing(doc, tr, isActive, docBlocker));

            string? currentTab = null;
            if (isActive)
            {
                try { currentTab = AcApp.GetSystemVariable("CTAB") as string; } catch { /* ignored */ }
            }

            var layouts = new List<Layout>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout l && (!l.ModelType || ctx.GetBool("modelLayout")))
                    layouts.Add(l);
            }

            ModelSpaceIndex? msIndex = null;
            foreach (var layout in layouts.OrderBy(l => l.TabOrder))
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    var item = ReadLayout(layout, tr, ctx, options, docBlocker, currentTab);
                    if (ctx.GetBool("paperSpaceObjects") && !layout.ModelType)
                    {
                        if (ctx.GetBool("modelThroughViewports"))
                            msIndex ??= ModelSpaceIndex.Build(db, tr, ctx);
                        ReadPaperSpace(layout, tr, item, ctx, options, docBlocker, msIndex);
                    }
                    ctx.Items.Add(item);
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
        finally
        {
            _objects.Journal.Flush();
        }
    }

    private DataItem ReadDrawing(Document doc, Transaction tr, bool isActive, string? docBlocker)
    {
        var db = doc.Database;
        var item = new DataItem
        {
            Id = AcadDocumentProvider.Id(doc),
            ItemType = "Drawing",
            Name = Path.GetFileName(doc.Name),
            Key = Path.GetFileNameWithoutExtension(doc.Name),
        };

        var general = new PropertyGroup("Drawing");
        general.Properties.Add(ObjectPropertyReader.Derived("File Name", Path.GetFileName(doc.Name)));
        general.Properties.Add(ObjectPropertyReader.Derived("Path", doc.Name));
        general.Properties.Add(ObjectPropertyReader.Derived("Read Only", doc.IsReadOnly ? "Yes" : "No"));
        general.Properties.Add(ObjectPropertyReader.Derived("Active Document", isActive ? "Yes" : "No"));
        item.Groups.Add(general);

        var settings = new PropertyGroup("Drawing settings");
        foreach (var pv in _objects.Managed.ReadNamed(db, tr, DatabaseSettings, docBlocker))
        {
            pv.Source = PropertySource.Setting;
            settings.Properties.Add(pv);
        }
        item.Groups.Add(settings);

        if (isActive)
        {
            var sysvars = new PropertyGroup("System variables (active drawing)");
            foreach (var (name, readOnly) in SystemVariables)
            {
                object? v;
                try { v = AcApp.GetSystemVariable(name); }
                catch { continue; }
                var f = ValueFormatter.Format(v, tr);
                sysvars.Properties.Add(new PropertyValue
                {
                    Name = name,
                    Id = name,
                    Source = PropertySource.Setting,
                    StorageType = f.StorageType,
                    Value = f.Display,
                    RawValue = f.Raw,
                    HasValue = f.HasValue,
                    IsReadOnly = readOnly || docBlocker is not null,
                    ReadOnlyReason = readOnly ? "Read-only system variable" : docBlocker,
                });
            }
            item.Groups.Add(sysvars);
        }
        return item;
    }

    private DataItem ReadLayout(Layout layout, Transaction tr, ReadContext ctx, ObjectReadOptions options,
        string? docBlocker, string? currentTab)
    {
        // Generic read of the Layout object (page setup/plot settings via COM + .NET).
        var layoutOptions = new ObjectReadOptions { Com = options.Com, Managed = options.Managed, Attributes = false, TableCells = false };
        var item = _objects.Read(layout, tr, layoutOptions, docBlocker);
        item.ItemType = "Layout";
        item.Name = layout.LayoutName;
        item.Key = layout.LayoutName;

        var summary = new PropertyGroup("Layout");
        summary.Properties.Add(ObjectPropertyReader.Derived("Layout Name", layout.LayoutName));
        summary.Properties.Add(ObjectPropertyReader.Derived("Tab Order", layout.TabOrder.ToString(Inv)));
        summary.Properties.Add(ObjectPropertyReader.Derived("Page Setup", SafeGet(() => layout.PlotSettingsName)));
        summary.Properties.Add(ObjectPropertyReader.Derived("Plotter", SafeGet(() => layout.PlotConfigurationName)));
        summary.Properties.Add(ObjectPropertyReader.Derived("Paper Size", SafeGet(() => layout.CanonicalMediaName)));
        summary.Properties.Add(ObjectPropertyReader.Derived("Plot Style Table", SafeGet(() => layout.CurrentStyleSheet)));
        summary.Properties.Add(ObjectPropertyReader.Derived("Current Tab", currentTab is null ? "" :
            string.Equals(currentTab, layout.LayoutName, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No"));
        item.Groups.Insert(0, summary);

        // "View" category: the overall paper space viewport.
        var overall = OverallViewport(layout, tr);
        if (overall is not null)
        {
            var view = new PropertyGroup("Paper space view");
            foreach (var pv in _objects.Managed.ReadNamed(overall, tr, PaperViewProps, docBlocker))
            {
                pv.OwnerId = overall.ObjectId.Handle.ToString();
                view.Properties.Add(pv);
            }
            item.Groups.Insert(1, view);
        }
        else if (!layout.ModelType)
        {
            ctx.Warn($"Layout '{layout.LayoutName}' has not been initialized (never opened), so its view settings are not available.");
        }
        return item;
    }

    private void ReadPaperSpace(Layout layout, Transaction tr, DataItem layoutItem, ReadContext ctx,
        ObjectReadOptions options, string? docBlocker, ModelSpaceIndex? msIndex)
    {
        int max = ctx.GetInt("maxPaperSpaceObjects", 5000);
        int maxModel = ctx.GetInt("maxModelObjectsPerViewport", 500);
        var overallId = OverallViewport(layout, tr)?.ObjectId ?? ObjectId.Null;
        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

        int count = 0;
        foreach (ObjectId id in btr)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (id == overallId) continue;
            if (++count > max)
            {
                ctx.Truncated = true;
                ctx.Warn($"Layout '{layout.LayoutName}': stopped after {max} paper space objects.");
                break;
            }
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
                var child = _objects.Read(obj, tr, options, docBlocker);
                if (obj is Viewport vp && msIndex is not null)
                    AddModelObjects(vp, tr, child, ctx, options, docBlocker, msIndex, maxModel, layout.LayoutName);
                layoutItem.Children.Add(child);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ctx.Warn($"Layout '{layout.LayoutName}', object {id.Handle}: {ex.Message}");
            }
        }
    }

    private void AddModelObjects(Viewport vp, Transaction tr, DataItem vpItem, ReadContext ctx, ObjectReadOptions options,
        string? docBlocker, ModelSpaceIndex index, int max, string layoutName)
    {
        var clip = ViewportClip.From(vp);
        if (clip is null)
        {
            ctx.Warn($"Layout '{layoutName}', viewport {vp.ObjectId.Handle}: not a plan view; model objects skipped.");
            return;
        }
        if (vp.NonRectClipOn)
            ctx.Warn($"Layout '{layoutName}', viewport {vp.ObjectId.Handle}: has a non-rectangular clip; using its rectangular extents.");

        var frozen = new HashSet<ObjectId>();
        try
        {
            var ids = vp.GetFrozenLayers();
            if (ids is not null) foreach (ObjectId id in ids) frozen.Add(id);
        }
        catch { /* ignored */ }

        int n = 0;
        foreach (var (id, ext, layerId) in index.Entries)
        {
            if (frozen.Contains(layerId) || index.HiddenLayers.Contains(layerId)) continue;
            if (!clip.Overlaps(ext)) continue;
            if (++n > max)
            {
                ctx.Truncated = true;
                ctx.Warn($"Layout '{layoutName}', viewport {vp.ObjectId.Handle}: stopped after {max} model objects.");
                break;
            }
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
                var child = _objects.Read(obj, tr, options, docBlocker);
                child.ItemType = "Model: " + child.ItemType;
                vpItem.Children.Add(child);
            }
            catch (Exception ex)
            {
                ctx.Warn($"Model object {id.Handle}: {ex.Message}");
            }
        }
    }

    private static Viewport? OverallViewport(Layout layout, Transaction tr)
    {
        try
        {
            var ids = layout.GetViewports();
            if (ids is not null && ids.Count > 0)
                return tr.GetObject(ids[0], OpenMode.ForRead) as Viewport;
        }
        catch { /* not initialized */ }

        if (layout.ModelType) return null;
        // Fallback: the first viewport in the layout's block is the paper space viewport.
        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        foreach (ObjectId id in btr)
            if (id.ObjectClass.DxfName == "VIEWPORT")
                return tr.GetObject(id, OpenMode.ForRead) as Viewport;
        return null;
    }

    private static string SafeName(Layout l)
    {
        try { return l.LayoutName; } catch { return "?"; }
    }

    private static string SafeGet(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }

    /// <summary>Model space entities with their extents, built once per read.</summary>
    private sealed class ModelSpaceIndex
    {
        public List<(ObjectId Id, Autodesk.AutoCAD.DatabaseServices.Extents3d Extents, ObjectId LayerId)> Entries { get; } = new();
        public HashSet<ObjectId> HiddenLayers { get; } = new();

        public static ModelSpaceIndex Build(Database db, Transaction tr, ReadContext ctx)
        {
            var index = new ModelSpaceIndex();
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId lid in layers)
            {
                if (tr.GetObject(lid, OpenMode.ForRead) is LayerTableRecord ltr && (ltr.IsOff || ltr.IsFrozen))
                    index.HiddenLayers.Add(lid);
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int skipped = 0;
            foreach (ObjectId id in ms)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false, true) is not Entity e || !e.Visible) continue;
                    var ext = e.GeometricExtents;
                    index.Entries.Add((id, ext, e.LayerId));
                }
                catch
                {
                    skipped++; // no extents (empty text, rays, ...)
                }
            }
            if (skipped > 0) ctx.Warn($"{skipped} model space objects have no extents and were ignored for viewport matching.");
            return index;
        }
    }
}
