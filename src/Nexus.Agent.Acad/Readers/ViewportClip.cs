using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexus.Agent.Acad.Readers;

/// <summary>
/// The model-space region a (plan) viewport shows, as a 2D convex polygon in WCS,
/// with a separating-axis overlap test against an object's extents.
/// </summary>
internal sealed class ViewportClip
{
    private readonly Point2d[] _poly;

    private ViewportClip(Point2d[] poly) => _poly = poly;

    /// <summary>Null when the viewport is not a plan view (3D views are not supported).</summary>
    public static ViewportClip? From(Viewport vp)
    {
        if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis)) return null;
        if (vp.Height <= 0 || vp.Width <= 0 || vp.ViewHeight <= 0) return null;

        Matrix3d dcsToWcs =
            Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
            Matrix3d.Displacement(vp.ViewTarget.GetAsVector()) *
            Matrix3d.PlaneToWorld(vp.ViewDirection);

        double h = vp.ViewHeight;
        double w = h * vp.Width / vp.Height;
        Point2d c = vp.ViewCenter;
        var dcs = new[]
        {
            new Point3d(c.X - w / 2, c.Y - h / 2, 0),
            new Point3d(c.X + w / 2, c.Y - h / 2, 0),
            new Point3d(c.X + w / 2, c.Y + h / 2, 0),
            new Point3d(c.X - w / 2, c.Y + h / 2, 0),
        };
        return new ViewportClip(dcs.Select(p => p.TransformBy(dcsToWcs)).Select(p => new Point2d(p.X, p.Y)).ToArray());
    }

    public bool Overlaps(Extents3d ext)
    {
        var box = new[]
        {
            new Point2d(ext.MinPoint.X, ext.MinPoint.Y),
            new Point2d(ext.MaxPoint.X, ext.MinPoint.Y),
            new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y),
            new Point2d(ext.MinPoint.X, ext.MaxPoint.Y),
        };
        return !Separated(_poly, box) && !Separated(box, _poly);
    }

    private static bool Separated(Point2d[] a, Point2d[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            var p1 = a[i];
            var p2 = a[(i + 1) % a.Length];
            var axis = new Vector2d(-(p2.Y - p1.Y), p2.X - p1.X);
            if (axis.Length < 1e-12) continue;
            (double minA, double maxA) = Project(a, axis);
            (double minB, double maxB) = Project(b, axis);
            if (maxA < minB || maxB < minA) return true;
        }
        return false;
    }

    private static (double, double) Project(Point2d[] pts, Vector2d axis)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in pts)
        {
            double d = p.X * axis.X + p.Y * axis.Y;
            if (d < min) min = d;
            if (d > max) max = d;
        }
        return (min, max);
    }
}
