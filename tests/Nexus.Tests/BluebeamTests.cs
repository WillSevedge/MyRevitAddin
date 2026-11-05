using Nexus.Hub.Core.Files;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Nexus.Tests;

public sealed class BluebeamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-bb-" + Guid.NewGuid().ToString("N"));

    public BluebeamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    private string MakePdf(string name, int pages)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++) doc.AddPage();
        string p = Path.Combine(_dir, name);
        doc.Save(p);
        return p;
    }

    private string LabelledSet()
    {
        string target = Path.Combine(_dir, "Set.pdf");
        PdfTools.Combine(new[]
        {
            new PdfPart(MakePdf("A101.pdf", 1), "A101", "A101 - Floor Plan"),
            new PdfPart(MakePdf("A102.pdf", 2), "A102", "A102 - Roof Plan"),
            new PdfPart(MakePdf("Cover.pdf", 1)),
        }, target, "Issue set");
        return target;
    }

    [Fact]
    public void Combined_sets_are_labelled_and_bookmarked_by_sheet()
    {
        using var doc = PdfReader.Open(LabelledSet(), PdfDocumentOpenMode.Import);
        Assert.Equal(4, doc.PageCount);
        Assert.Equal(new[] { "A101", "A102", "A102 (2)", "4" }, PdfMarkups.PageLabels(doc));
        var bookmarks = PdfMarkups.BookmarkTitles(doc);
        Assert.Equal("A101 - Floor Plan", bookmarks[0]);
        Assert.Equal("A102 - Roof Plan", bookmarks[1]);
        Assert.Equal("Issue set", doc.Info.Title);
    }

    [Fact]
    public void Markups_are_read_with_their_sheet_replies_and_status()
    {
        string path = LabelledSet();
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            PdfDictionary Annot(PdfPage page, string subtype, Action<PdfDictionary> fill)
            {
                var a = new PdfDictionary(doc);
                a.Elements["/Type"] = new PdfName("/Annot");
                a.Elements["/Subtype"] = new PdfName(subtype);
                a.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(50), new PdfReal(50));
                fill(a);
                doc.Internals.AddObject(a);
                if (page.Elements.GetArray("/Annots") is not { } annots)
                {
                    annots = new PdfArray(doc);
                    page.Elements["/Annots"] = annots;
                }
                annots.Elements.Add(a.Reference!);
                return a;
            }
            var cloud = Annot(doc.Pages[1], "/Polygon", a =>
            {
                a.Elements["/IT"] = new PdfName("/PolygonCloud");
                a.Elements["/Subj"] = new PdfString("Cloud+");
                a.Elements["/Contents"] = new PdfString("Add the door tag");
                a.Elements["/T"] = new PdfString("J. Reviewer");
                a.Elements["/M"] = new PdfString("D:20261012093000-05'00'");
            });
            Annot(doc.Pages[1], "/Text", a =>
            {
                a.Elements["/IRT"] = cloud.Reference!;
                a.Elements["/Contents"] = new PdfString("Done in Revit");
                a.Elements["/T"] = new PdfString("Drafter");
                a.Elements["/M"] = new PdfString("D:20261013100000");
            });
            Annot(doc.Pages[1], "/Text", a =>
            {
                a.Elements["/IRT"] = cloud.Reference!;
                a.Elements["/State"] = new PdfString("Completed");
                a.Elements["/StateModel"] = new PdfString("Review");
                a.Elements["/T"] = new PdfString("Drafter");
                a.Elements["/M"] = new PdfString("D:20261013100500");
            });
            Annot(doc.Pages[3], "/FreeText", a =>
            {
                a.Elements["/IT"] = new PdfName("/FreeTextCallout");
                a.Elements["/Contents"] = new PdfString("Cover: update the date");
                a.Elements["/T"] = new PdfString("PM");
            });
            Annot(doc.Pages[0], "/Link", _ => { });
            doc.Save(path);
        }

        var markups = PdfMarkups.Read(path);
        Assert.Equal(2, markups.Count);
        var c = markups.Single(m => m.Type == "Cloud");
        Assert.Equal(("A102", 2, "Cloud+", "Add the door tag", "J. Reviewer"), (c.Sheet, c.Page, c.Subject, c.Comment, c.Author));
        Assert.Equal("Completed", c.Status);
        Assert.False(c.IsOpen);
        Assert.Equal(1, c.Replies);
        Assert.Equal("Drafter: Done in Revit", c.ReplyTexts.Single());
        Assert.Equal(new DateTime(2026, 10, 12, 9, 30, 0), c.Date);

        var callout = markups.Single(m => m.Type == "Callout");
        Assert.Equal("", callout.Sheet); // the cover page has only a page number
        Assert.True(callout.IsOpen);
        Assert.Equal(4, callout.Page);
    }

    [Fact]
    public void A_single_sheet_pdf_is_matched_by_its_file_name()
    {
        string path = MakePdf("A201 - Elevations.pdf", 1);
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var a = new PdfDictionary(doc);
            a.Elements["/Type"] = new PdfName("/Annot");
            a.Elements["/Subtype"] = new PdfName("/Square");
            a.Elements["/Contents"] = new PdfString("Check the grid bubbles");
            doc.Internals.AddObject(a);
            var annots = new PdfArray(doc);
            annots.Elements.Add(a.Reference!);
            doc.Pages[0].Elements["/Annots"] = annots;
            doc.Save(path);
        }
        var m = Assert.Single(PdfMarkups.Read(path));
        Assert.Equal("A201", m.Sheet);
        Assert.Equal("Rectangle", m.Type);
    }

    [Theory]
    [InlineData("A101 - Floor Plan", "A101")]
    [InlineData("A-101: Plan", "A-101")]
    [InlineData("A101", "A101")]
    [InlineData("12", "")]
    public void Sheet_numbers_come_from_labels_and_titles(string text, string sheet) => Assert.Equal(sheet, PdfMarkups.SheetOf(text));
}
