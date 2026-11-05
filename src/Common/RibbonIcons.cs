using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nexus.Agent;

/// <summary>
/// Ribbon button images for the Revit and AutoCAD add-ins: the Nexus N (white on dark ribbons,
/// black on light ribbons) with a small dot showing the hub connection state.
/// Linked into both add-in projects (needs WPF).
/// </summary>
internal static class RibbonIcons
{
    public static Color StateColor(AgentState state) => state switch
    {
        AgentState.Connected => Color.FromRgb(0x2E, 0x9E, 0x44),
        AgentState.Listening => Color.FromRgb(0x2F, 0x6F, 0xD6),
        AgentState.Faulted => Color.FromRgb(0xD0, 0x3A, 0x2F),
        _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
    };

    /// <summary>The N at <paramref name="size"/> (16 or 32) with the status dot in the lower right corner.</summary>
    public static ImageSource Create(AgentState state, int size, bool darkUi)
    {
        var logo = Load($"Nexus-N-{(darkUi ? "white" : "black")}-{(size <= 16 ? 16 : 32)}.png");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (logo is not null) dc.DrawImage(logo, new Rect(0, 0, size, size));
            double r = size * 0.19;
            var center = new Point(size - r - 0.5, size - r - 0.5);
            var fill = new SolidColorBrush(StateColor(state));
            fill.Freeze();
            // Ring in the ribbon's own background colour, so the dot stands off the N.
            var ring = new Pen(darkUi ? new SolidColorBrush(Color.FromRgb(0x3B, 0x44, 0x53)) : Brushes.White, Math.Max(1, size / 16.0));
            ring.Freeze();
            dc.DrawEllipse(fill, ring, center, r, r);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// A Windows icon-font glyph (Segoe Fluent Icons, or Segoe MDL2 Assets on Windows 10) for a tool button,
    /// in the ribbon's text colour.
    /// </summary>
    public static ImageSource Glyph(string glyph, int size, bool darkUi)
    {
        var font = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var brush = darkUi ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        brush.Freeze();
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size * 0.875, brush, 1.0);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapImage? Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
