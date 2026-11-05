using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Bulk;

namespace Nexus.Tests;

public sealed class BulkRenameTests
{
    private static readonly HostInfo Revit = new() { HostKind = HostKinds.Revit, Product = "Revit", Version = "2026", ProcessId = 1, Features = { AgentFeatures.Write } };
    private const string Number = "Sheet › Number";
    private const string Title = "Sheet › Title";

    private static DataItem Sheet(string number, string title) => new()
    {
        Id = "uid-" + number, ItemType = "Sheet", Name = number, Key = number,
        Groups =
        {
            new PropertyGroup("Sheet · Identity Data")
            {
                Properties =
                {
                    new PropertyValue { Name = "Sheet Number", Id = "SHEET_NUMBER", Value = number, RawValue = number, Source = PropertySource.BuiltIn, StorageType = "String" },
                    new PropertyValue { Name = "Sheet Name", Id = "SHEET_NAME", Value = title, RawValue = title, Source = PropertySource.BuiltIn, StorageType = "String" },
                },
            },
        },
    };

    private static List<TableRow> Rows(params DataItem[] sheets) => ResultTable.Build(new[]
    {
        new ResultSource { Host = Revit, DocumentTitle = "Tower.rvt", Result = new ReadResult { ReaderId = "s", DocumentId = "rvt", Items = sheets.ToList() } },
    }, sheetFields: SheetFieldMap.Default()).Rows;

    private static List<RenamePreview> Preview(List<TableRow> rows, string column, RenameOptions o, IReadOnlyList<TableRow>? all = null, bool unique = true) =>
        BulkRename.Preview(rows, column, o, (r, c) => r.Values.TryGetValue(c, out var v) ? v.Value : null, Editing.Blocker, unique, all);

    [Theory]
    [InlineData("A1##", 1, "A101")]
    [InlineData("A1##", 12, "A112")]
    [InlineData("M-#", 7, "M-7")]
    [InlineData("E", 3, "E3")]
    [InlineData("C-###A", 5, "C-005A")]
    public void Patterns_number_as_expected(string pattern, int n, string expected) =>
        Assert.Equal(expected, BulkRename.Sequence(pattern, n));

    [Fact]
    public void Renumbering_in_order_with_a_step()
    {
        var rows = Rows(Sheet("X1", "A"), Sheet("X2", "B"), Sheet("X3", "C"));
        var p = Preview(rows, Number, new RenameOptions { Pattern = "A1##", Start = 1, Step = 2 });
        Assert.Equal(new[] { "A101", "A103", "A105" }, p.Select(x => x.New));
        Assert.All(p, x => Assert.Equal(RenameStatus.Change, x.Status));
    }

    [Fact]
    public void Swapping_numbers_is_not_a_clash_but_taking_a_kept_one_is()
    {
        var all = Rows(Sheet("A101", "A"), Sheet("A102", "B"), Sheet("A103", "C"));
        // Renumber the first two in reverse: A101→A102 and A102→A101 is a swap (allowed).
        var swap = Preview(new List<TableRow> { all[1], all[0] }, Number, new RenameOptions { Pattern = "A10#", Start = 1 }, all);
        Assert.All(swap, x => Assert.Equal(RenameStatus.Change, x.Status));

        // Only A101 renumbered to A103: A103 keeps its number, so that is a clash.
        var clash = Preview(new List<TableRow> { all[0] }, Number, new RenameOptions { Pattern = "A10#", Start = 3 }, all);
        Assert.Equal(RenameStatus.Duplicate, Assert.Single(clash).Status);
    }

    [Fact]
    public void Replace_with_wildcards_and_whole_value()
    {
        var rows = Rows(Sheet("A101", "FIRST FLOOR PLAN"), Sheet("A102", "SECOND FLOOR PLAN"), Sheet("A103", "ROOF"));
        var p = Preview(rows, Title, new RenameOptions { Mode = RenameMode.Replace, Find = "FLOOR PLAN", ReplaceWith = "LEVEL PLAN" }, unique: false);
        Assert.Equal(new[] { "FIRST LEVEL PLAN", "SECOND LEVEL PLAN", "ROOF" }, p.Select(x => x.New));
        Assert.Equal(RenameStatus.NoChange, p[2].Status);

        var w = Preview(rows, Title, new RenameOptions { Mode = RenameMode.Replace, Find = "* FLOOR*", ReplaceWith = "PLAN", WholeValue = true }, unique: false);
        Assert.Equal(new[] { "PLAN", "PLAN", "ROOF" }, w.Select(x => x.New));
    }

    [Fact]
    public void Case_prefix_and_suffix()
    {
        int n = 0;
        Assert.Equal("First Floor Plan - Area of Work", BulkRename.Apply("FIRST FLOOR PLAN - AREA OF WORK", new RenameOptions { Mode = RenameMode.ChangeCase, Case = TextCase.Title }, ref n));
        Assert.Equal("E-101", BulkRename.Apply("101", new RenameOptions { Mode = RenameMode.AddText, Prefix = "E-" }, ref n));
        Assert.Equal("101", BulkRename.Apply("E-101-OLD", new RenameOptions { Mode = RenameMode.RemoveText, Prefix = "e-", Suffix = "-OLD" }, ref n));
    }
}
