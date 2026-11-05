using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nexus.Hub.Pdf;

/// <summary>
/// Preview and printing of sheet PDFs with Windows' own PDF renderer (Windows.Data.Pdf): no PDF program
/// needed. Printing sends each page as an image fitted to the paper, for check prints and half-size sets.
/// </summary>
internal static class PdfPages
{
    /// <summary>Page count and the first page's size in inches.</summary>
    public static async Task<(int Pages, double WidthIn, double HeightIn)> InfoAsync(string path)
    {
        var doc = await Load(path);
        if (doc.PageCount == 0) return (0, 0, 0);
        using var page = doc.GetPage(0);
        return ((int)doc.PageCount, page.Size.Width / 96.0, page.Size.Height / 96.0);
    }

    /// <summary>One page as a bitmap, at most <paramref name="longSide"/> pixels on its long side.</summary>
    public static async Task<BitmapSource> RenderAsync(string path, int pageIndex, int longSide)
    {
        var doc = await Load(path);
        using var page = doc.GetPage((uint)Math.Clamp(pageIndex, 0, (int)doc.PageCount - 1));
        return await Render(page, longSide);
    }

    private static async Task<BitmapSource> Render(PdfPage page, int longSide)
    {
        double w = page.Size.Width, h = page.Size.Height;
        double scale = longSide / Math.Max(w, h);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(w * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(h * scale)),
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream.AsStream();
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task<PdfDocument> Load(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        return await PdfDocument.LoadFromFileAsync(file);
    }

    /// <summary>
    /// Prints every page of <paramref name="pdfs"/> to <paramref name="queue"/>: each page turned to fit the paper
    /// (landscape sheets print landscape) and scaled to the printable area. With <paramref name="matchSheetSize"/>
    /// each page asks for paper of its own size (plotters); otherwise the paper chosen in the print dialog is used.
    /// </summary>
    public static async Task PrintAsync(IReadOnlyList<string> pdfs, PrintQueue queue, PrintTicket ticket, bool matchSheetSize,
        int dpi, IProgress<string>? progress, CancellationToken ct)
    {
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var collator = writer.CreateVisualsCollator(ticket, ticket);
        collator.BeginBatchWrite();
        try
        {
            int n = 0;
            foreach (var path in pdfs)
            {
                ct.ThrowIfCancellationRequested();
                var doc = await Load(path);
                for (uint i = 0; i < doc.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Printing {Path.GetFileNameWithoutExtension(path)} ({++n})…");
                    using var page = doc.GetPage(i);
                    double w = page.Size.Width, h = page.Size.Height; // 1/96 inch
                    bool landscape = w > h;

                    var pageTicket = ticket.Clone();
                    pageTicket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                    if (matchSheetSize) pageTicket.PageMediaSize = new PageMediaSize(Math.Min(w, h), Math.Max(w, h));
                    var area = queue.GetPrintCapabilities(pageTicket).PageImageableArea;

                    // Printable area in the page's orientation.
                    double ox = area?.OriginWidth ?? 0, oy = area?.OriginHeight ?? 0;
                    double aw = area?.ExtentWidth ?? (pageTicket.PageMediaSize?.Width ?? w);
                    double ah = area?.ExtentHeight ?? (pageTicket.PageMediaSize?.Height ?? h);
                    if (landscape != (aw > ah))
                    {
                        (aw, ah) = (ah, aw);
                        (ox, oy) = (oy, ox);
                    }

                    // Enough pixels for the paper at the chosen resolution (capped to keep memory sane).
                    int longSide = (int)Math.Min(12000, Math.Max(aw, ah) / 96.0 * dpi);
                    var image = await Render(page, Math.Max(1000, longSide));
                    double scale = Math.Min(aw / w, ah / h);
                    double dw = w * scale, dh = h * scale;
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                        dc.DrawImage(image, new Rect(ox + (aw - dw) / 2, oy + (ah - dh) / 2, dw, dh));
                    collator.Write(visual, pageTicket);
                }
            }
        }
        finally
        {
            collator.EndBatchWrite();
        }
    }
}
