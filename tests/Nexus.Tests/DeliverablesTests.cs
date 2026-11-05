using ClosedXML.Excel;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class DeliverablesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-dl-" + Guid.NewGuid().ToString("N"));

    public DeliverablesTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    /// <summary>Laid out like the Budget Tool: title rows, headers in row 9 next to a comparison area, Model Export.</summary>
    private string Workbook()
    {
        string path = Path.Combine(_dir, "Budget.xlsx");
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Project Summary").Cell(1, 1).Value = "Summary";
        var ws = wb.AddWorksheet("Deliverables List");
        ws.Cell(1, 1).Value = "Project Deliverables List";
        string[] headers = { "Discipline", "Type", "Dwg/Doc No.", "Rev.", "Date", "Status", "Drawing Title / Document Description", "Task", "Dup. Check", "", "In Model?", "Model Sheet Name", "Title Match?", "Model Rev" };
        for (int c = 0; c < headers.Length; c++) ws.Cell(9, c + 1).Value = headers[c];
        void Row(int r, string disc, string no, string rev, string status, string title)
        {
            ws.Cell(r, 1).Value = disc; ws.Cell(r, 2).Value = "Drawing"; ws.Cell(r, 3).Value = no; ws.Cell(r, 4).Value = rev;
            ws.Cell(r, 6).Value = status; ws.Cell(r, 7).Value = title; ws.Cell(r, 14).Value = "ignored";
        }
        ws.Cell(10, 1).Value = "PM"; ws.Cell(10, 2).Value = "Document"; ws.Cell(10, 7).Value = "Scope of Work"; // no number yet
        Row(11, "Civil", "C-101", "1", "IFC", "SITE PLAN");
        Row(12, "Civil", "C-102", "0", "IFR", "GRADING PLAN");
        Row(13, "Structural", "S-101", "A", "IFC", "FOUNDATION PLAN");
        var me = wb.AddWorksheet("Model Export");
        string[] mh = { "Source", "Sheet Number", "Sheet Name", "Rev", "Rev Date", "Status", "On Deliverables List?" };
        for (int c = 0; c < mh.Length; c++) me.Cell(4, c + 1).Value = mh[c];
        me.Range(4, 1, 6, mh.Length).CreateTable("ModelExport");
        for (int r = 5; r <= 6; r++) me.Cell(r, 7).FormulaA1 = $"IF(B{r}=\"\",\"\",\"x\")";
        wb.SaveAs(path);
        return path;
    }

    private static ResultTable Model(params (string No, string Name, string Rev, string Status)[] sheets) => ResultTable.Build(new[]
    {
        new ResultSource
        {
            Host = new HostInfo { HostKind = HostKinds.Revit, Product = "Revit" }, DocumentTitle = "Site.rvt",
            Result = new ReadResult
            {
                ReaderId = "revit.sheets",
                Items = sheets.Select((s, i) => new DataItem
                {
                    Id = i.ToString(), ItemType = "Sheet", Name = s.No,
                    Groups =
                    {
                        new PropertyGroup("Identity Data")
                        {
                            Properties =
                            {
                                new PropertyValue { Name = "Sheet Number", Id = "SHEET_NUMBER", Value = s.No },
                                new PropertyValue { Name = "Sheet Name", Id = "SHEET_NAME", Value = s.Name },
                                new PropertyValue { Name = "Sheet Status", Id = "x", Value = s.Status, Source = PropertySource.Shared },
                            },
                        },
                        new PropertyGroup("Current Revision")
                        {
                            Properties = { new PropertyValue { Name = "Revision Number", Value = s.Rev, Source = PropertySource.Derived } },
                        },
                    },
                }).ToList(),
            },
        },
    }, sheetFields: SheetFieldMap.Default());

    [Fact]
    public void The_deliverables_list_is_found_and_read()
    {
        var wb = Deliverables.Read(Workbook());
        Assert.Equal("Deliverables List", wb.ListSheet);
        Assert.Equal(1, wb.Unnumbered);
        Assert.Equal(new[] { "C-101", "C-102", "S-101" }, wb.Items.Select(i => i.Number));
        var c101 = wb.Items[0];
        Assert.Equal(("SITE PLAN", "1", "IFC", "Civil"), (c101.Title, c101.Rev, c101.Status, c101.Discipline));
        Assert.Equal("ModelExport", wb.ModelExport?.Name);
        Assert.Contains("On Deliverables List?", wb.ModelExport!.FormulaHeaders);
    }

    [Fact]
    public void The_list_is_compared_with_the_model_by_number()
    {
        var wb = Deliverables.Read(Workbook());
        var model = Model(("C-101", "SITE PLAN", "1", "IFC"), ("C-102", "GRADING", "1", "IFR"), ("C-900", "DETAILS", "0", "IFR"));
        var c = DeliverablesCompare.Compare(wb.Items, model).ToDictionary(x => x.Number);
        Assert.Equal(DeliverableResult.Match, c["C-101"].Result);
        Assert.Equal(DeliverableResult.Differs, c["C-102"].Result);
        Assert.True(c["C-102"].TitleDiffers);
        Assert.True(c["C-102"].RevDiffers);
        Assert.False(c["C-102"].StatusDiffers);
        Assert.Equal(DeliverableResult.NotInModel, c["S-101"].Result);
        Assert.Equal(DeliverableResult.NotOnList, c["C-900"].Result);
        Assert.Equal("Revit", c["C-900"].Program);
    }

    [Fact]
    public void Model_export_is_the_only_thing_written()
    {
        string path = Workbook();
        var wb = Deliverables.Read(path);
        var model = Model(("C-101", "SITE PLAN", "1", "IFC"), ("C-102", "GRADING", "1", "IFR"), ("C-900", "DETAILS", "0", "IFR"));
        var link = Deliverables.ModelExportLink(path, wb.ModelExport!, model, SheetFieldMap.Default());
        Assert.All(link.Columns, c => Assert.Equal(ExcelDirection.ToExcel, c.Direction));
        Assert.True(link.Columns.Single(c => c.Header == "On Deliverables List?").IsFormula);

        var data = ExcelWorkbook.Read(link);
        var plan = ExcelWorkbook.PlanFill(link, data, ExcelFill.Ordered(ExcelFill.Rows(model, link), link));
        Assert.Equal("Model Export", plan.Worksheet);
        ExcelWorkbook.WriteFill(link, plan);

        var after = Deliverables.Read(path);
        Assert.Equal(wb.Items.Select(i => (i.Number, i.Title, i.Rev)), after.Items.Select(i => (i.Number, i.Title, i.Rev)));
        var written = ExcelWorkbook.Read(link);
        Assert.Equal(new[] { "C-101", "C-102", "C-900" }, written.Rows.Select(r => r.Key));
        Assert.Equal("Revit", written.Rows[0].Cells["Source"].Text);
    }

    [Fact]
    public void Model_export_with_the_lists_own_headings_is_filled()
    {
        // Model Export laid out with the deliverables list's headings ("Dwg/Doc No." did not match before).
        string path = Path.Combine(_dir, "Headings.xlsx");
        using (var book = new XLWorkbook())
        {
            book.AddWorksheet("Deliverables List").Cell(9, 1).Value = "Dwg/Doc No.";
            var me = book.AddWorksheet("Model Export");
            string[] mh = { "Dwg/Doc No.", "Drawing Title / Document Description", "Rev.", "Status" };
            for (int c = 0; c < mh.Length; c++) me.Cell(1, c + 1).Value = mh[c];
            me.Range(1, 1, 2, mh.Length).CreateTable("ModelExport");
            book.SaveAs(path);
        }
        var wb = Deliverables.Read(path);
        var model = Model(("C-101", "SITE PLAN", "1", "IFC"), ("C-102", "GRADING", "1", "IFR"));
        var link = Deliverables.ModelExportLink(path, wb.ModelExport!, model, SheetFieldMap.Default());
        Assert.Equal("Dwg/Doc No.", link.KeyHeader);
        Assert.True(ExcelSources.WritesExcel(link.Columns.Single(c => c.Header == "Drawing Title / Document Description")));

        var plan = ExcelWorkbook.PlanFill(link, ExcelWorkbook.Read(link), ExcelFill.Ordered(ExcelFill.Rows(model, link), link));
        ExcelWorkbook.WriteFill(link, plan);
        var written = ExcelWorkbook.Read(link);
        Assert.Equal(new[] { "C-101", "C-102" }, written.Rows.Select(r => r.Key));
        Assert.Equal("SITE PLAN", written.Rows[0].Cells["Drawing Title / Document Description"].Text);
    }

    /// <summary>A one-layout DWG whose title block has a title but no number attribute.</summary>
    private static ResultSource Dwg(string file, string title) => new()
    {
        Host = new HostInfo { HostKind = HostKinds.AutoCAD, Product = "AutoCAD" }, DocumentTitle = file,
        Result = new ReadResult
        {
            ReaderId = "acad.sheets",
            Items =
            {
                new DataItem
                {
                    Id = file, ItemType = "Layout", Name = "Layout1",
                    Groups = { new PropertyGroup("Title Block") { Properties = { new PropertyValue { Name = "TITLE", Id = "TITLE", Value = title } } } },
                },
            },
        },
    };

    [Fact]
    public void Drawings_without_a_number_are_matched_by_file_name()
    {
        var wb = Deliverables.Read(Workbook()); // the list has C-101, C-102 (GRADING PLAN), S-101
        var model = ResultTable.Build(new[]
        {
            Dwg("C-102 - GRADING PLAN.dwg", "GRADING"),
            Dwg("C-10 Old.dwg", "OLD"),          // C-10 is not C-101 or C-102: not matched
        }, sheetFields: SheetFieldMap.Default());
        var c = DeliverablesCompare.Compare(wb.Items, model);

        var c102 = c.Single(x => x.Number == "C-102");
        Assert.True(c102.MatchedByFile);
        Assert.Equal(DeliverableResult.Differs, c102.Result);
        Assert.True(c102.TitleDiffers);
        Assert.Equal(DeliverableResult.NotInModel, c.Single(x => x.Number == "C-101").Result);
        // The unmatched drawing is listed, not dropped.
        Assert.Contains(c, x => x.Result == DeliverableResult.NotOnList && x.File == "C-10 Old.dwg");
    }

    [Theory]
    [InlineData("C-101.dwg", "C-101", true)]
    [InlineData("C-101 - SITE PLAN.dwg", "C-101", true)]
    [InlineData("Job 42_C-101_Rev2.dwg", "C-101", true)]
    [InlineData("C-1010.dwg", "C-101", false)]
    [InlineData("XC-101.dwg", "C-101", false)]
    public void File_names_hold_the_number_as_a_whole_token(string file, string number, bool expected) =>
        Assert.Equal(expected, DeliverablesCompare.FileNameHas(file, number));
}
