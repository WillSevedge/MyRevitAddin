using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Nexus.Hub.Core.Files;

/// <summary>One markup (PDF annotation) as Bluebeam Revu's Markups list shows it.</summary>
public sealed class PdfMarkup
{
    public string File { get; init; } = "";
    /// <summary>1-based page number.</summary>
    public int Page { get; init; }
    /// <summary>The page's label (the sheet number in sets made by Nexus or most publishers), else "".</summary>
    public string PageLabel { get; init; } = "";
    /// <summary>
    /// The sheet the page is: from the page label, else the page's bookmark, else the file name of a one-sheet PDF
    /// ("A101 - Floor Plan" → A101).
    /// </summary>
    public string Sheet { get; init; } = "";
    /// <summary>"Cloud", "Callout", "Rectangle"… (the annotation kind).</summary>
    public string Type { get; init; } = "";
    /// <summary>Bluebeam's Subject column (often the tool, e.g. "Cloud+", or a custom subject).</summary>
    public string Subject { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime? Date { get; init; }
    /// <summary>The latest review status set on the markup ("Accepted", "Completed"… or a custom status); "" when none.</summary>
    public string Status { get; init; } = "";
    public int Replies { get; init; }
    /// <summary>The replies, oldest first ("Author: text").</summary>
    public List<string> ReplyTexts { get; init; } = new();

    /// <summary>Still to do: no status, or a status that does not close it.</summary>
    public bool IsOpen => !PdfMarkups.ClosedStatuses.Contains(Status);
}

/// <summary>
/// Reads markups from PDFs (Bluebeam Revu keeps its markups in the PDF as standard annotations): author, subject,
/// comment, date, replies and review status, with each page matched to its sheet by page label or bookmark.
/// Links, form fields and pop-up notes are not markups and are left out. The file is only read.
/// </summary>
public static class PdfMarkups
{
    /// <summary>Statuses that close a markup (Bluebeam's and the PDF standard's).</summary>
    public static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accepted", "Completed", "Rejected", "Cancelled", "Canceled", "Closed", "Resolved", "Done", "Void",
    };

    private static readonly HashSet<string> NotMarkups = new(StringComparer.Ordinal) { "/Popup", "/Link", "/Widget" };

    public static List<PdfMarkup> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var labels = PageLabels(doc);
        var bookmarks = BookmarkTitles(doc);

        // Every annotation once, so replies (IRT) find their markup on any page.
        var all = new List<(int Page, PdfDictionary Annot)>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (doc.Pages[i].Elements.GetArray("/Annots") is not { } annots) continue;
            foreach (var item in annots.Elements)
                if (Deref(item) is PdfDictionary a && !NotMarkups.Contains(a.Elements.GetName("/Subtype"))) all.Add((i, a));
        }

        var replies = new Dictionary<PdfDictionary, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
        foreach (var (_, a) in all)
            if (Deref(a.Elements["/IRT"]) is PdfDictionary parent)
            {
                if (!replies.TryGetValue(parent, out var list)) replies[parent] = list = new List<PdfDictionary>();
                list.Add(a);
            }

        var markups = new List<PdfMarkup>();
        string file = Path.GetFileName(path);
        // One sheet per file ("A101 - Floor Plan.pdf"): the file name says which sheet it is. Used when the page has
        // no label or bookmark naming it, for a single page, or a file without labels and bookmarks named by number.
        string named = SheetOf(Path.GetFileNameWithoutExtension(path));
        string fromFileName = doc.PageCount == 1 || (labels.All(l => SheetOf(l).Length == 0) && bookmarks.Count == 0 && named.Any(char.IsDigit))
            ? named : "";
        foreach (var (page, a) in all)
        {
            if (a.Elements.ContainsKey("/IRT")) continue; // a reply or a status change, listed with its markup
            var own = replies.TryGetValue(a, out var r) ? r.OrderBy(x => DateOf(x) ?? DateTime.MinValue).ToList() : new List<PdfDictionary>();
            // Review status: the latest state set by a reply (or on the markup itself).
            string status = own.Where(x => x.Elements.ContainsKey("/State")).Select(x => Text(x, "/State")).LastOrDefault()
                            ?? Text(a, "/State");
            var texts = own.Where(x => !x.Elements.ContainsKey("/State") && Text(x, "/Contents").Length > 0)
                .Select(x => $"{Text(x, "/T")}: {Text(x, "/Contents")}").ToList();

            string label = labels.Count > page ? labels[page] : "";
            string sheet = SheetOf(label);
            if (sheet.Length == 0 && bookmarks.TryGetValue(page, out var bookmark)) sheet = SheetOf(bookmark);
            if (sheet.Length == 0) sheet = fromFileName;
            markups.Add(new PdfMarkup
            {
                File = file,
                Page = page + 1,
                PageLabel = label,
                Sheet = sheet,
                Type = TypeOf(a),
                Subject = Text(a, "/Subj"),
                Comment = Text(a, "/Contents"),
                Author = Text(a, "/T"),
                Date = DateOf(a),
                Status = status == "None" ? "" : status,
                Replies = texts.Count,
                ReplyTexts = texts,
            });
        }
        return markups;
    }

    /// <summary>"A101 - Floor Plan" or "A101 Floor Plan" → "A101"; a label that is only a page number gives "".</summary>
    public static string SheetOf(string labelOrTitle)
    {
        string t = labelOrTitle.Trim();
        if (t.Length == 0 || t.All(char.IsDigit)) return "";
        foreach (var sep in new[] { " - ", " – ", " — ", ": " })
        {
            int i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return t[..i].Trim();
        }
        return t;
    }

    // ------------------------------------------------------------------ page labels and bookmarks

    /// <summary>Each page's label from the catalog's /PageLabels number tree ("" when the file has none).</summary>
    public static List<string> PageLabels(PdfDocument doc)
    {
        var result = new List<string>();
        if (Deref(doc.Internals.Catalog.Elements["/PageLabels"]) is not PdfDictionary tree) return result;
        var ranges = new SortedDictionary<int, PdfDictionary>();
        CollectNums(tree, ranges, 0);
        if (ranges.Count == 0) return result;
        for (int i = 0; i < doc.PageCount; i++)
        {
            var start = ranges.Keys.LastOrDefault(k => k <= i, -1);
            if (start < 0)
            {
                result.Add("");
                continue;
            }
            var range = ranges[start];
            string prefix = Text(range, "/P");
            int first = range.Elements.ContainsKey("/St") ? range.Elements.GetInteger("/St") : 1;
            int n = first + (i - start);
            string style = range.Elements.GetName("/S");
            string number = style switch
            {
                "/D" => n.ToString(CultureInfo.InvariantCulture),
                "/R" => Roman(n).ToUpperInvariant(),
                "/r" => Roman(n),
                "/A" => Letters(n),
                "/a" => Letters(n).ToLowerInvariant(),
                _ => "",
            };
            result.Add(prefix + number);
        }
        return result;
    }

    private static void CollectNums(PdfDictionary node, SortedDictionary<int, PdfDictionary> ranges, int depth)
    {
        if (depth > 20) return;
        if (Deref(node.Elements["/Nums"]) is PdfArray nums)
            for (int k = 0; k + 1 < nums.Elements.Count; k += 2)
                if (Deref(nums.Elements[k]) is PdfInteger index && Deref(nums.Elements[k + 1]) is PdfDictionary range)
                    ranges[index.Value] = range;
        if (Deref(node.Elements["/Kids"]) is PdfArray kids)
            foreach (var kid in kids.Elements)
                if (Deref(kid) is PdfDictionary child) CollectNums(child, ranges, depth + 1);
    }

    /// <summary>Page index → the first bookmark that opens it (top-level and nested).</summary>
    public static Dictionary<int, string> BookmarkTitles(PdfDocument doc)
    {
        var pages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++) pages[doc.Pages[i]] = i;
        var result = new Dictionary<int, string>();
        void Walk(PdfDictionary? item, int depth)
        {
            for (int guard = 0; item is not null && guard < 100000 && depth < 20; guard++)
            {
                string title = Text(item, "/Title");
                if (title.Length > 0 && DestinationPage(item) is { } page && pages.TryGetValue(page, out int index))
                    result.TryAdd(index, title);
                Walk(Deref(item.Elements["/First"]) as PdfDictionary, depth + 1);
                item = Deref(item.Elements["/Next"]) as PdfDictionary;
            }
        }
        if (Deref(doc.Internals.Catalog.Elements["/Outlines"]) is PdfDictionary outlines)
            Walk(Deref(outlines.Elements["/First"]) as PdfDictionary, 0);
        return result;
    }

    private static PdfDictionary? DestinationPage(PdfDictionary item)
    {
        var dest = Deref(item.Elements["/Dest"]);
        if (dest is null && Deref(item.Elements["/A"]) is PdfDictionary action && action.Elements.GetName("/S") == "/GoTo")
            dest = Deref(action.Elements["/D"]);
        return dest is PdfArray array && array.Elements.Count > 0 ? Deref(array.Elements[0]) as PdfDictionary : null;
    }

    // ------------------------------------------------------------------ values

    private static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static string Text(PdfDictionary d, string key) => Deref(d.Elements[key]) switch
    {
        PdfString s => s.Value.Replace("\r\n", "\n").Replace('\r', '\n').Trim(),
        PdfName n => n.Value.TrimStart('/'),
        _ => "",
    };

    private static DateTime? DateOf(PdfDictionary d)
    {
        string raw = Text(d, "/M");
        if (raw.Length == 0) raw = Text(d, "/CreationDate");
        if (raw.StartsWith("D:", StringComparison.Ordinal)) raw = raw[2..];
        if (raw.Length < 8) return null;
        string digits = new string(raw.TakeWhile(char.IsDigit).ToArray()).PadRight(14, '0')[..14];
        return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    /// <summary>Bluebeam's names for the annotation kinds (Revu sets /IT for clouds, callouts and measurements).</summary>
    private static string TypeOf(PdfDictionary a)
    {
        string subtype = a.Elements.GetName("/Subtype").TrimStart('/');
        string intent = a.Elements.GetName("/IT").TrimStart('/');
        if (intent.Contains("Cloud", StringComparison.OrdinalIgnoreCase)) return "Cloud";
        if (intent == "FreeTextCallout") return "Callout";
        if (intent.StartsWith("Line", StringComparison.Ordinal) && intent.Contains("Dimension")) return "Length";
        if (intent.StartsWith("Polygon", StringComparison.Ordinal) && intent.Contains("Dimension")) return "Area";
        if (intent.StartsWith("PolyLine", StringComparison.Ordinal) && intent.Contains("Dimension")) return "Perimeter";
        return subtype switch
        {
            "FreeText" => "Text Box",
            "Square" => "Rectangle",
            "Circle" => "Ellipse",
            "PolyLine" => "Polyline",
            "Ink" => "Pen",
            "Text" => "Note",
            "StrikeOut" => "Strikethrough",
            "FileAttachment" => "Attachment",
            "" => "Markup",
            _ => subtype,
        };
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n > 3999) return n.ToString(CultureInfo.InvariantCulture);
        var map = new[] { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
        var sb = new System.Text.StringBuilder();
        foreach (var (value, text) in map)
            while (n >= value) { sb.Append(text); n -= value; }
        return sb.ToString();
    }

    /// <summary>PDF letter numbering: A…Z, then AA…ZZ, AAA…</summary>
    private static string Letters(int n)
    {
        if (n <= 0) return "";
        char c = (char)('A' + (n - 1) % 26);
        return new string(c, (n - 1) / 26 + 1);
    }
}
