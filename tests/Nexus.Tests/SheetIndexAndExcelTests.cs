using ClosedXML.Excel;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class SheetIndexAndExcelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-excel-" + Guid.NewGuid().ToString("N"));

    public SheetIndexAndExcelTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    private static HostInfo Revit => new() { HostKind = HostKinds.Revit, Product = "Revit", Version = "2026", ProcessId = 1, Features = { AgentFeatures.Write } };
    private static HostInfo Acad => new() { HostKind = HostKinds.AutoCAD, Product = "Civil 3D", Version = "2026", ProcessId = 2, Features = { AgentFeatures.Write } };

    private static PropertyValue P(string name, string value, string? id = null, PropertySource source = PropertySource.BuiltIn) => new()
    {
        Name = name, Id = id ?? name, Value = value, RawValue = value, Source = source, StorageType = "String", OwnerId = "owner-" + name,
    };

    private static DataItem RevitSheet(string number, string name, string drawnBy, string rev) => new()
    {
        Id = "uid-" + number, ItemType = "Sheet", Name = $"{number} - {name}", Key = number,
        Groups =
        {
            new PropertyGroup("Sheet · Identity Data")
            {
                Properties = { P("Sheet Number", number, "SHEET_NUMBER"), P("Sheet Name", name, "SHEET_NAME"), P("Drawn By", drawnBy, "SHEET_DRAWN_BY") },
            },
            new PropertyGroup("Current Revision") { Properties = { P("Revision Number", rev, source: PropertySource.Derived) } },
        },
    };

    private static DataItem AcadLayout(string layout, string dwgNo, string title) => new()
    {
        Id = "h-" + layout, ItemType = "Layout", Name = layout, Key = layout,
        Groups =
        {
            new PropertyGroup("Layout") { Properties = { P("Layout Name", layout, "Layout:Name", PropertySource.Managed) } },
            new PropertyGroup("Title Block")
            {
                Properties = { P("DWG_NO", dwgNo, "A1", PropertySource.Attribute), P("SHEET_TITLE", title, "A2", PropertySource.Attribute) },
            },
        },
    };

    private static ResultTable Table() => ResultTable.Build(new[]
    {
        new ResultSource { Host = Revit, DocumentTitle = "Tower.rvt",
            Result = new ReadResult { ReaderId = "revit.sheets", DocumentId = "rvt-1", Items = { RevitSheet("A-101", "Plan", "WS", "2"), RevitSheet("A-102", "Section", "WS", "1") } } },
        new ResultSource { Host = Acad, DocumentTitle = "C-Grading.dwg",
            Result = new ReadResult { ReaderId = "acad.sheets", DocumentId = "dwg-1", Items = { AcadLayout("Layout1", "C-201", "GRADING PLAN") } } },
    }, sheetFields: SheetFieldMap.Default());

    [Fact]
    public void Sheet_fields_unify_revit_parameters_and_title_block_tags()
    {
        var t = Table();
        Assert.Equal("Sheet › Number", t.Columns[0].Id);
        Assert.Contains(t.Columns, c => c.Id == "Sheet › Title");

        var revit = t.Rows[0];
        Assert.Equal("A-101", revit.Values["Sheet › Number"].Value);
        Assert.Equal("Plan", revit.Values["Sheet › Title"].Value);
        Assert.Equal("2", revit.Values["Sheet › Revision"].Value);
        Assert.Equal("WS", revit.Values["Sheet › Drawn By"].Value);

        var acad = t.Rows[2];
        Assert.Equal("C-201", acad.Values["Sheet › Number"].Value);       // DWG_NO tag, not the layout name
        Assert.Equal("GRADING PLAN", acad.Values["Sheet › Title"].Value);  // SHEET_TITLE tag
        // The field is the same property object, so an edit goes to the attribute.
        Assert.Same(acad.Values["Title Block › DWG_NO"], acad.Values["Sheet › Number"]);
    }

    [Fact]
    public void Layout_name_is_the_sheet_name_when_there_is_no_title_block()
    {
        var bare = new DataItem
        {
            Id = "h-L2", ItemType = "Layout", Name = "C-301 Utility Plan", Key = "C-301 Utility Plan",
            Groups = { new PropertyGroup("Layout") { Properties = { P("Layout Name", "C-301 Utility Plan", "Layout:Name", PropertySource.Managed) } } },
        };
        var t = ResultTable.Build(new[]
        {
            new ResultSource { Host = Acad, DocumentTitle = "C-Utility.dwg",
                Result = new ReadResult { ReaderId = "acad.sheets", DocumentId = "dwg-2", Items = { bare } } },
        }, sheetFields: SheetFieldMap.Default());
        var row = Assert.Single(t.Rows);
        Assert.Equal("C-301 Utility Plan", row.Values["Sheet › Title"].Value);
        Assert.False(row.Values.ContainsKey("Sheet › Number"));
        // Editing the sheet name renames the layout.
        Assert.Same(row.Values["Layout › Layout Name"], row.Values["Sheet › Title"]);
    }

    [Fact]
    public void Saved_field_map_moves_layout_name_from_number_to_name()
    {
        var map = SheetFieldMap.Default();
        var layout = map.Fields.Single(f => f.Name == "Title").Rules.Last();
        map.Fields.Single(f => f.Name == "Title").Rules.Remove(layout);
        map.Fields.Single(f => f.Name == SheetFieldMap.NumberField).Rules.Add(layout);

        Assert.True(SheetFieldMap.MoveLayoutNameToTitle(map));
        Assert.DoesNotContain(map.Fields.Single(f => f.Name == SheetFieldMap.NumberField).Rules, r => r.Group == "Layout");
        Assert.Contains(map.Fields.Single(f => f.Name == "Title").Rules, r => r.Group == "Layout");
        Assert.False(SheetFieldMap.MoveLayoutNameToTitle(map));
    }

    [Fact]
    public void Excel_link_compares_and_writes_both_ways()
    {
        string path = Path.Combine(_dir, "Sheet Index.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Index");
            ws.Cell(1, 1).Value = "PROJECT SHEET INDEX";
            ws.Cell(3, 1).Value = "Sheet No.";
            ws.Cell(3, 2).Value = "Sheet Title";
            ws.Cell(3, 3).Value = "Rev";
            ws.Cell(3, 4).Value = "Notes";
            ws.Cell(4, 1).Value = "A-101"; ws.Cell(4, 2).Value = "Plan"; ws.Cell(4, 3).Value = 3;
            ws.Cell(5, 1).Value = "C-201"; ws.Cell(5, 2).Value = "Grading Plan"; ws.Cell(5, 3).Value = "";
            ws.Cell(6, 1).Value = "Z-999"; ws.Cell(6, 2).Value = "Only in Excel";
            wb.SaveAs(path);
        }

        var info = Assert.Single(ExcelWorkbook.Inspect(path));
        Assert.Equal(3, info.HeaderRow);
        Assert.Equal(new[] { "Sheet No.", "Sheet Title", "Rev", "Notes" }, info.Headers);

        var table = Table();
        var link = new ExcelLink
        {
            WorkbookPath = path, Worksheet = "Index", HeaderRow = 3,
            Columns = ExcelCompare.AutoMap(info.Headers, table, SheetFieldMap.Default()),
        };
        Assert.Equal("Sheet › Number", link.Columns[0].ColumnId);
        Assert.Equal("Sheet › Title", link.Columns[1].ColumnId);
        Assert.Equal("Sheet › Revision", link.Columns[2].ColumnId);
        Assert.Equal("", link.Columns[3].ColumnId);
        Assert.Equal("Sheet No.", link.KeyHeader);

        var warnings = new List<string>();
        var diffs = ExcelCompare.Compare(table, ExcelWorkbook.Read(link), link, warnings);
        Assert.Empty(warnings);

        Assert.Contains(diffs, d => d.Kind == ExcelDiffKind.Different && d.Key == "A-101" && d.Header == "Rev" && d.ModelValue == "2" && d.ExcelValue == "3");
        Assert.Contains(diffs, d => d.Kind == ExcelDiffKind.Different && d.Key == "C-201" && d.Header == "Sheet Title" && d.ModelValue == "GRADING PLAN");
        Assert.Contains(diffs, d => d.Kind == ExcelDiffKind.OnlyInModel && d.Key == "A-102");
        Assert.Contains(diffs, d => d.Kind == ExcelDiffKind.OnlyInExcel && d.Key == "Z-999");
        Assert.DoesNotContain(diffs, d => d.Key == "A-101" && d.Header == "Sheet Title"); // same value

        // Model → Excel: fix C-201's title, append A-102.
        var titleDiff = diffs.Single(d => d.Key == "C-201" && d.Header == "Sheet Title");
        string backup = ExcelWorkbook.Write(link,
            new[] { new ExcelCellUpdate(titleDiff.Cell!.Row, titleDiff.Cell.Column, titleDiff.ModelValue!) },
            new[] { new Dictionary<string, string> { ["Sheet No."] = "A-102", ["Sheet Title"] = "Section", ["Rev"] = "1" } });
        Assert.True(File.Exists(backup));

        var again = ExcelCompare.Compare(table, ExcelWorkbook.Read(link), link, new List<string>());
        Assert.DoesNotContain(again, d => d.Key == "C-201");
        Assert.DoesNotContain(again, d => d.Key == "A-102");
        Assert.Contains(again, d => d.Key == "A-101" && d.Header == "Rev"); // still differs (not written)

        using var check = new XLWorkbook(path);
        var sheet = check.Worksheet("Index");
        Assert.Equal("A-102", sheet.Cell(7, 1).GetString()); // appended below the last row (Z-999 at row 6)
        Assert.Equal(1.0, sheet.Cell(7, 3).GetDouble());     // numbers stay numbers
    }

    [Theory]
    [InlineData("A-101", " A-101 ", true)]
    [InlineData("1", "1.0", true)]
    [InlineData("Plan", "PLAN", false)]
    [InlineData("", null, true)]
    public void Values_compare_as_text_numbers_or_dates(string? a, string? b, bool same) =>
        Assert.Equal(same, ExcelCompare.SameValue(a, b));

    [Fact]
    public void Natural_sort_orders_sheet_numbers() =>
        Assert.Equal(new[] { "A-2", "A-10", "B-1" }, new[] { "A-10", "B-1", "A-2" }.OrderBy(x => x, NaturalComparer.Instance));
}

public class GridEditTests
{
    [Fact]
    public void Edits_count_while_the_row_is_still_being_edited_and_after_commit()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("c0", typeof(string));
        dt.Columns.Add("c1", typeof(string));
        dt.Rows.Add("A-101", "Plan");
        dt.Rows.Add("A-102", "Section");
        dt.AcceptChanges();

        var row = dt.Rows[0];
        var view = dt.DefaultView[0];
        view.BeginEdit();          // the WPF DataGrid edits rows like this
        view["c1"] = "Floor Plan"; // cell committed, row still in edit
        Assert.True(GridEdits.IsEdited(row, "c1", out var original));
        Assert.Equal("Plan", original);
        Assert.False(GridEdits.IsEdited(row, "c0", out _));
        Assert.Equal("Floor Plan", GridEdits.Value(row, "c1"));

        view.EndEdit();            // row committed
        Assert.True(GridEdits.IsEdited(row, "c1", out _));
        Assert.False(GridEdits.MayBeEdited(dt.Rows[1]));

        view.BeginEdit();
        view["c1"] = "Plan";       // typed back to the original value
        view.EndEdit();
        Assert.False(GridEdits.IsEdited(row, "c1", out _));
    }
}
