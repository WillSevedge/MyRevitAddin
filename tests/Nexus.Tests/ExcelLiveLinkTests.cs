using ClosedXML.Excel;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class ExcelLiveLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-live-" + Guid.NewGuid().ToString("N"));

    public ExcelLiveLinkTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    private static readonly string[] Headers = { "Source", "Sheet Number", "Sheet Name", "Rev", "Rev Date", "Status", "On List?" };

    /// <summary>A landing tab like the Budget Tool's Model Export: a title, then a 4-row table with a formula column.</summary>
    private string LandingWorkbook()
    {
        string path = Path.Combine(_dir, "Budget.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Model Export");
        ws.Cell(1, 1).Value = "Model Export - Landing Tab";
        for (int c = 0; c < Headers.Length; c++) ws.Cell(4, c + 1).Value = Headers[c];
        var table = ws.Range(4, 1, 8, Headers.Length).CreateTable("ModelExport");
        for (int r = 5; r <= 8; r++) ws.Cell(r, 7).FormulaA1 = $"IF(B{r}=\"\",\"\",\"Yes\")";
        wb.AddWorksheet("Notes").Cell(1, 1).Value = "keep me";
        wb.SaveAs(path);
        return path;
    }

    private static ExcelLink Link(string path, ExcelTableInfo t) => new()
    {
        WorkbookPath = path, Worksheet = t.Worksheet, HeaderRow = t.HeaderRow, Table = t.Name, Mode = ExcelLinkMode.Fill,
        KeyColumnId = "num",
        Columns =
        {
            new() { Header = "Source", ColumnId = ExcelSources.Program, Direction = ExcelDirection.ToExcel },
            new() { Header = "Sheet Number", ColumnId = "num" },
            new() { Header = "Sheet Name", ColumnId = "name" },
            new() { Header = "Rev", ColumnId = "rev", Direction = ExcelDirection.ToExcel },
            new() { Header = "Status", ColumnId = ExcelSources.Fixed, FixedText = "IFC", Direction = ExcelDirection.ToExcel },
            new() { Header = "On List?", IsFormula = true, Direction = ExcelDirection.None },
        },
    };

    private static List<IReadOnlyDictionary<string, string>> Rows(int n) => Enumerable.Range(1, n)
        .Select(i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Source"] = "Revit", ["Sheet Number"] = $"A-{100 + i}", ["Sheet Name"] = $"PLAN {i}", ["Rev"] = "B", ["Status"] = "IFC",
        }).ToList();

    [Fact]
    public void Tables_and_formula_columns_are_found()
    {
        var t = Assert.Single(ExcelWorkbook.Inspect(LandingWorkbook()).SelectMany(s => s.Tables));
        Assert.Equal("ModelExport", t.Name);
        Assert.Equal(4, t.HeaderRow);
        Assert.Equal(5, t.FirstDataRow);
        Assert.Equal(8, t.LastRow);
        Assert.Equal(Headers, t.Headers);
        Assert.Contains("On List?", t.FormulaHeaders);
    }

    [Fact]
    public void Fill_writes_rows_grows_the_table_and_clears_leftovers()
    {
        string path = LandingWorkbook();
        var t = ExcelWorkbook.Inspect(path).SelectMany(s => s.Tables).Single();
        var link = Link(path, t);

        var plan = ExcelWorkbook.PlanFill(link, ExcelWorkbook.Read(link), Rows(6));
        Assert.Equal(10, plan.GrowToRow); // 6 rows from row 5
        Assert.DoesNotContain(plan.Updates, u => u.Column == 7); // never the formula column
        ExcelWorkbook.WriteFill(link, plan);

        var data = ExcelWorkbook.Read(link);
        Assert.Equal(10, data.TableLastRow);
        Assert.Equal(new[] { "A-101", "A-102", "A-103", "A-104", "A-105", "A-106" }, data.Rows.Select(r => r.Key));
        Assert.Equal("PLAN 6", data.Area[10].Cells["Sheet Name"].Text);
        Assert.Contains("On List?", data.FormulaHeaders); // formulas kept (the column is read-only)
        Assert.True(ExcelWorkbook.PlanFill(link, data, Rows(6)).IsEmpty); // nothing to do twice

        var fewer = ExcelWorkbook.PlanFill(link, data, Rows(2));
        Assert.Equal(4, fewer.ClearedRows);
        ExcelWorkbook.WriteFill(link, fewer);
        Assert.Equal(2, ExcelWorkbook.Read(link).Rows.Count);

        using var wb = new XLWorkbook(path);
        Assert.True(wb.Worksheet("Model Export").Cell(6, 7).HasFormula);
        Assert.Equal("keep me", wb.Worksheet("Notes").Cell(1, 1).GetString());
        Assert.Equal("Model Export - Landing Tab", wb.Worksheet("Model Export").Cell(1, 1).GetString());
    }

    [Fact]
    public void Match_mode_fills_empty_table_rows_before_growing()
    {
        string path = LandingWorkbook();
        var t = ExcelWorkbook.Inspect(path).SelectMany(s => s.Tables).Single();
        var link = Link(path, t);
        link.Mode = ExcelLinkMode.Match;
        var data = ExcelWorkbook.Read(link);
        var plan = ExcelWorkbook.PlanMatch(link, data, Array.Empty<ExcelCellUpdate>(), Rows(5));
        Assert.Equal(9, plan.GrowToRow);
        ExcelWorkbook.WriteFill(link, plan);
        Assert.Equal(5, ExcelWorkbook.Read(link).Rows.Count);
    }

    [Fact]
    public void Budget_tool_headers_are_matched_automatically()
    {
        var table = ResultTable.Build(new[]
        {
            new ResultSource
            {
                Host = new HostInfo { HostKind = HostKinds.Revit, Product = "Revit" }, DocumentTitle = "Tower.rvt",
                Result = new ReadResult
                {
                    ReaderId = "revit.sheets",
                    Items =
                    {
                        new DataItem
                        {
                            Id = "1", ItemType = "Sheet", Name = "A101",
                            Groups =
                            {
                                new PropertyGroup("Identity Data")
                                {
                                    Properties =
                                    {
                                        new PropertyValue { Name = "Sheet Number", Id = "SHEET_NUMBER", Value = "A101" },
                                        new PropertyValue { Name = "Sheet Name", Id = "SHEET_NAME", Value = "Plan" },
                                        new PropertyValue { Name = "Sheet Status", Id = "x", Value = "IFC", Source = PropertySource.Shared },
                                    },
                                },
                                new PropertyGroup("Current Revision")
                                {
                                    Properties =
                                    {
                                        new PropertyValue { Name = "Revision Number", Value = "2", Source = PropertySource.Derived },
                                        new PropertyValue { Name = "Revision Date", Value = "10/03/2026", Source = PropertySource.Derived },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }, sheetFields: SheetFieldMap.Default());

        var map = ExcelCompare.AutoMap(new[] { "Source", "Sheet Number", "Sheet Name", "Rev", "Rev Date", "Status", "On Deliverables List?" }, table, SheetFieldMap.Default());
        string Id(string h) => map.Single(m => m.Header == h).ColumnId;
        Assert.Equal(ExcelSources.Program, Id("Source"));
        Assert.Equal(ExcelDirection.ToExcel, map.Single(m => m.Header == "Source").Direction);
        Assert.Equal(SheetFieldMap.NumberColumnId, Id("Sheet Number"));
        Assert.Equal(SheetFieldMap.ColumnId("Title"), Id("Sheet Name"));
        Assert.Equal(SheetFieldMap.ColumnId("Revision"), Id("Rev"));
        Assert.Equal(SheetFieldMap.ColumnId("Revision Date"), Id("Rev Date"));
        Assert.Equal(SheetFieldMap.ColumnId("Status"), Id("Status"));
        Assert.Equal("", Id("On Deliverables List?"));

        var row = Assert.Single(table.Rows);
        Assert.Equal("Revit", ExcelSources.ValueFor(map[0], row));
        Assert.Equal("IFC", row.Values[SheetFieldMap.ColumnId("Status")].Value);
        var rows = ExcelFill.Rows(table, new ExcelLink { KeyColumnId = SheetFieldMap.NumberColumnId, Columns = map });
        Assert.Equal("2", Assert.Single(rows).Values["Rev"]);
        Assert.Equal("Tower.rvt", rows[0].Document);
    }

    [Fact]
    public void Rows_of_files_that_are_not_open_are_kept()
    {
        IReadOnlyDictionary<string, string> R(string n) => new Dictionary<string, string> { ["Sheet Number"] = n };
        var memory = new ExcelFillMemory();
        var both = memory.Merge(new[] { ("A.rvt", R("A-1")), ("B.dwg", R("C-1")) }, new[] { "A.rvt", "B.dwg" });
        Assert.Equal(2, both.Count);
        // Only A open now, with a new sheet: B's rows stay.
        var onlyA = memory.Merge(new[] { ("A.rvt", R("A-1")), ("A.rvt", R("A-2")) }, new[] { "A.rvt" });
        Assert.Equal(new[] { "A-1", "A-2", "C-1" }, onlyA.Select(r => r.Values["Sheet Number"]).OrderBy(x => x));
        Assert.Equal(new[] { "B.dwg" }, memory.Closed(new[] { "A.rvt" }));
        // A open with no sheets left: its rows go.
        var aEmpty = memory.Merge(Array.Empty<(string, IReadOnlyDictionary<string, string>)>(), new[] { "A.rvt" });
        Assert.Equal(new[] { "C-1" }, aEmpty.Select(r => r.Values["Sheet Number"]));

        var link = new ExcelLink { KeyColumnId = "num", Columns = { new() { Header = "Sheet Number", ColumnId = "num" } } };
        var ordered = ExcelFill.Ordered(new[] { ("x", R("A-10")), ("x", R("A-2")) }, link);
        Assert.Equal(new[] { "A-2", "A-10" }, ordered.Select(r => r["Sheet Number"]));
    }

    [Fact]
    public void Saved_field_maps_get_the_status_field()
    {
        var map = SheetFieldMap.Default();
        map.Fields.RemoveAll(f => f.Name == "Status");
        Assert.True(SheetFieldMap.AddMissingFields(map));
        Assert.Contains(map.Fields, f => f.Name == "Status");
        Assert.False(SheetFieldMap.AddMissingFields(map));
    }
}
