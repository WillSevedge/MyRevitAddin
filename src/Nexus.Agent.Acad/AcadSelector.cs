using System.Globalization;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nexus.Agent.Acad;

/// <summary>
/// "Show in AutoCAD": makes the drawing current, switches to the layout (or the space
/// the objects live in), selects the objects (grips) and zooms to them.
/// </summary>
internal sealed class AcadSelector : IHostSelector<Document>
{
    public SelectResult Select(Document doc, IReadOnlyList<string> itemIds, AgentLog log)
    {
        var docs = AcApp.DocumentManager;
        if (!ReferenceEquals(docs.MdiActiveDocument, doc))
            docs.MdiActiveDocument = doc;

        var db = doc.Database;
        var ed = doc.Editor;
        using var docLock = doc.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();

        var objects = new List<DBObject>();
        foreach (var id in itemIds)
        {
            if (!long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) continue; // e.g. the drawing item
            if (!db.TryGetObjectId(new Handle(h), out var oid) || oid.IsErased) continue;
            objects.Add(tr.GetObject(oid, OpenMode.ForRead));
        }
        if (objects.Count == 0)
        {
            tr.Commit();
            return new SelectResult { Message = $"Made '{Path.GetFileName(doc.Name)}' the current drawing." };
        }

        // A layout: switch to its tab.
        if (objects.Count == 1 && objects[0] is Layout layout)
        {
            LayoutManager.Current.CurrentLayout = layout.LayoutName;
            tr.Commit();
            ed.Regen();
            return new SelectResult { Shown = 1, Message = $"Switched to layout '{layout.LayoutName}'." };
        }

        // Objects (attributes resolve to their block): switch to the space they are in, select, zoom.
        var entities = objects
            .Select(o => o is AttributeReference ar ? tr.GetObject(ar.OwnerId, OpenMode.ForRead) : o)
            .OfType<Entity>()
            .GroupBy(e => e.ObjectId).Select(g => g.First())
            .ToList();
        if (entities.Count == 0)
        {
            tr.Commit();
            return new SelectResult { Message = "These items cannot be selected (they are not drawing objects)." };
        }

        var space = (BlockTableRecord)tr.GetObject(entities[0].OwnerId, OpenMode.ForRead);
        if (space.IsLayout)
        {
            var owningLayout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
            if (!string.Equals(LayoutManager.Current.CurrentLayout, owningLayout.LayoutName, StringComparison.OrdinalIgnoreCase))
                LayoutManager.Current.CurrentLayout = owningLayout.LayoutName;
        }

        Extents3d? extents = null;
        foreach (var e in entities)
        {
            try
            {
                var x = e.GeometricExtents;
                if (extents is { } acc) { acc.AddExtents(x); extents = acc; }
                else extents = x;
            }
            catch { /* no extents (e.g. empty text) */ }
        }
        tr.Commit();

        ed.SetImpliedSelection(entities.Select(e => e.ObjectId).ToArray());
        if (extents is { } ext) ZoomTo(ed, ext, log);
        return new SelectResult { Shown = entities.Count, Message = $"Selected {entities.Count} object(s) in '{Path.GetFileName(doc.Name)}'." };
    }

    internal static void ZoomTo(Autodesk.AutoCAD.EditorInput.Editor ed, Extents3d ext, AgentLog log)
    {
        try
        {
            using var view = ed.GetCurrentView();
            double w = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1e-6);
            double h = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1e-6);
            double ratio = view.Width / view.Height;
            // Keep the window's aspect ratio and leave a 25% margin around the objects.
            if (w / h > ratio) h = w / ratio; else w = h * ratio;
            view.Width = w * 1.25;
            view.Height = h * 1.25;
            var wcsCenter = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
            // The view center is in display coordinates; for plan views (no twist, top view) DCS = WCS - target.
            view.CenterPoint = new Point2d(wcsCenter.X - view.Target.X, wcsCenter.Y - view.Target.Y);
            ed.SetCurrentView(view);
        }
        catch (Exception ex)
        {
            log.Warn("Zoom to the selection failed.", ex);
        }
    }
}
