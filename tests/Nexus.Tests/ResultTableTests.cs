using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Tests;

public class ResultTableTests
{
    private static ResultSource Source(params DataItem[] items) => new()
    {
        Host = new HostInfo { Product = "Revit", Version = "2026", ProcessId = 42 },
        DocumentTitle = "Tower",
        Result = new ReadResult { ReaderId = "revit.sheets", Items = items.ToList() },
    };

    private static PropertyValue P(string name, string value) => new() { Name = name, Value = value };

    [Fact]
    public void Flattens_children_and_builds_columns_in_first_seen_order()
    {
        var layout = new DataItem
        {
            Name = "Layout1",
            ItemType = "Layout",
            Groups = { new PropertyGroup("Plot") { Properties = { P("Paper", "ARCH D") } } },
            Children =
            {
                new DataItem
                {
                    Name = "Block: TB", ItemType = "BlockReference",
                    Groups = { new PropertyGroup("Attributes") { Properties = { P("DWGNO", "C-2001"), P("TITLE", "GRADING") } } },
                },
            },
        };

        var t = ResultTable.Build(new[] { Source(layout) });

        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Layout1 / Block: TB", t.Rows[1].Path);
        Assert.Equal(1, t.Rows[1].Depth);
        Assert.Equal(new[] { "Plot › Paper", "Attributes › DWGNO", "Attributes › TITLE" }, t.Columns.Select(c => c.Id));
        Assert.Equal("C-2001", t.Rows[1].Values["Attributes › DWGNO"].Value);
    }

    [Fact]
    public void Duplicate_property_names_in_a_group_get_numbered()
    {
        var item = new DataItem
        {
            Name = "A-101",
            Groups = { new PropertyGroup("Sheet · Other") { Properties = { P("Checker", "shared"), P("Checker", "project") } } },
        };
        var t = ResultTable.Build(new[] { Source(item) });
        Assert.Equal("shared", t.Rows[0].Values["Sheet · Other › Checker"].Value);
        Assert.Equal("project", t.Rows[0].Values["Sheet · Other › Checker (2)"].Value);
    }

    [Fact]
    public void Csv_export_respects_column_selection_and_escapes()
    {
        var item = new DataItem
        {
            Name = "A-101",
            Groups = { new PropertyGroup("G") { Properties = { P("Title", "Plan, \"North\""), P("Formula", "=SUM(A1)"), P("Skip", "x") } } },
        };
        var t = ResultTable.Build(new[] { Source(item) });
        string path = Path.GetTempFileName();
        try
        {
            Exporters.WriteWideCsv(t, new[] { "G › Title", "G › Formula" }, path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("G › Title,G › Formula", lines[0]);
            Assert.EndsWith("\"Plan, \"\"North\"\"\",'=SUM(A1)", lines[1]);
            Assert.DoesNotContain("Skip", lines[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
