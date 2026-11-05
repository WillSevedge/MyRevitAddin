using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Nexus.Hub.Core.Files;

/// <summary>One sheet's PDF in a combined set: the page label (the sheet number) and the bookmark ("A101 - Floor Plan").</summary>
public sealed record PdfPart(string Path, string Label = "", string Bookmark = "");

public static class PdfTools
{
    /// <summary>One PDF with every page of <paramref name="pdfs"/>, in order (pages are copied, not re-rendered).</summary>
    public static void Combine(IEnumerable<string> pdfs, string target) => Combine(pdfs.Select(p => new PdfPart(p)), target);

    /// <summary>
    /// One PDF with every page of the parts, in order. Ready for Bluebeam Revu (and any PDF viewer): each sheet's
    /// first page is labelled with its sheet number (further pages "A101 (2)"…) and bookmarked with its title, so
    /// page navigation, hyperlinks and slip sheets work by sheet number. The bookmarks panel opens with the file.
    /// </summary>
    public static void Combine(IEnumerable<PdfPart> parts, string target, string? title = null)
    {
        using var output = new PdfDocument();
        var labels = new List<(int Page, string Label)>();
        foreach (var part in parts)
        {
            using var input = PdfReader.Open(part.Path, PdfDocumentOpenMode.Import);
            int first = output.PageCount;
            int n = 0;
            foreach (var page in input.Pages)
            {
                var added = output.AddPage(page);
                if (part.Label.Length > 0) labels.Add((output.PageCount - 1, n == 0 ? part.Label : $"{part.Label} ({n + 1})"));
                if (n == 0 && part.Bookmark.Length > 0) output.Outlines.Add(part.Bookmark, added, true);
                n++;
            }
        }
        if (output.PageCount == 0) throw new InvalidOperationException("There are no pages to combine.");

        if (labels.Count > 0) SetPageLabels(output, labels);
        if (output.Outlines.Count > 0) output.PageMode = PdfPageMode.UseOutlines;
        if (!string.IsNullOrWhiteSpace(title)) output.Info.Title = title;
        output.Info.Creator = "Nexus";

        string temp = target + ".tmp";
        output.Save(temp);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// /PageLabels in the catalog: one range per labelled page with the label as its prefix and no numbering, so
    /// the page shows exactly that label. Pages without a label get their page number.
    /// </summary>
    private static void SetPageLabels(PdfDocument doc, List<(int Page, string Label)> labels)
    {
        var byPage = labels.ToDictionary(l => l.Page, l => l.Label);
        var nums = new PdfArray(doc);
        for (int i = 0; i < doc.PageCount; i++)
        {
            var range = new PdfDictionary(doc);
            range.Elements["/Type"] = new PdfName("/PageLabel");
            if (byPage.TryGetValue(i, out var label)) range.Elements["/P"] = Text(label);
            else
            {
                range.Elements["/S"] = new PdfName("/D");
                range.Elements["/St"] = new PdfInteger(i + 1);
            }
            nums.Elements.Add(new PdfInteger(i));
            nums.Elements.Add(range);
        }
        var tree = new PdfDictionary(doc);
        tree.Elements["/Nums"] = nums;
        doc.Internals.Catalog.Elements["/PageLabels"] = tree;
    }

    internal static PdfString Text(string value) =>
        value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);
}
