using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.History;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class SnapshotTests : IDisposable
{
    private static readonly HostInfo Revit = new() { HostKind = HostKinds.Revit, Product = "Revit", Version = "2026", ProcessId = 1 };
    private readonly string _home = Path.Combine(Path.GetTempPath(), "nexus-snap-" + Guid.NewGuid().ToString("N"));

    public SnapshotTests() => Environment.SetEnvironmentVariable("NEXUS_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXUS_HOME", null);
        try { Directory.Delete(_home, true); } catch { /* best effort */ }
    }

    private static PropertyValue P(string name, string value, string id) => new()
    {
        Name = name, Id = id, Value = value, RawValue = value, Source = PropertySource.BuiltIn, StorageType = "String",
    };

    private static DataItem Sheet(string uid, string number, string title, string drawnBy = "WS") => new()
    {
        Id = uid, ItemType = "Sheet", Name = number, Key = number,
        Groups =
        {
            new PropertyGroup("Sheet · Identity Data")
            {
                Properties = { P("Sheet Number", number, "SHEET_NUMBER"), P("Sheet Name", title, "SHEET_NAME"), P("Drawn By", drawnBy, "SHEET_DRAWN_BY") },
            },
        },
    };

    private static SheetSnapshot Take(string file, params DataItem[] sheets) => SheetSnapshot.Capture(ResultTable.Build(new[]
    {
        new ResultSource { Host = Revit, DocumentTitle = file, Result = new ReadResult { ReaderId = "s", DocumentId = file, Items = sheets.ToList() } },
    }, sheetFields: SheetFieldMap.Default()).Rows, "test", SheetSnapshot.KindManual);

    [Fact]
    public void Comparison_finds_added_removed_and_changed_sheets()
    {
        var before = Take("Tower.rvt", Sheet("u1", "A101", "PLAN"), Sheet("u2", "A102", "ROOF"), Sheet("u3", "A103", "OLD"));
        var now = Take("Tower.rvt", Sheet("u1", "A101", "FLOOR PLAN"), Sheet("u2", "A201", "ROOF", drawnBy: "JD"), Sheet("u4", "A104", "NEW"));

        var c = SnapshotComparison.Compare(before, now);
        Assert.Equal(1, c.Added);
        Assert.Equal(1, c.Removed);
        Assert.Equal(2, c.Changed);

        var renumbered = c.Sheets.Single(s => s.Number == "A201");
        Assert.Equal(SheetDiffKind.Changed, renumbered.Kind); // matched by id, not removed + added
        Assert.Contains(renumbered.Fields, f => f.Field == "Sheet Number" && f.Before == "A102" && f.After == "A201");
        Assert.Contains(renumbered.Fields, f => f.Field == "Drawn By" && f.After == "JD");
        // The parameter behind the standard field is not listed a second time.
        Assert.Single(renumbered.Fields, f => f.Field.EndsWith("Sheet Number", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_not_open_are_not_reported_as_removed()
    {
        var before = Take("Site.rvt", Sheet("s1", "C101", "SITE"));
        before.Sheets.AddRange(Take("Tower.rvt", Sheet("u1", "A101", "PLAN")).Sheets);
        before.Files = new List<string> { "Site.rvt", "Tower.rvt" };
        var now = Take("Tower.rvt", Sheet("u1", "A101", "PLAN"));

        var c = SnapshotComparison.Compare(before, now);
        Assert.Empty(c.Sheets);
        Assert.Equal(new[] { "Site.rvt" }, c.FilesNotOpen);
    }

    [Fact]
    public void Snapshots_and_the_change_log_are_saved()
    {
        var snap = Take("Tower.rvt", Sheet("u1", "A101", "PLAN"));
        SnapshotStore.Save(snap);
        var listed = Assert.Single(SnapshotStore.List());
        Assert.Equal("A101", Assert.Single(listed.Sheets).Number);

        ChangeLog.Append(new[] { new ChangeLogEntry { File = "Tower.rvt", Item = "A101", Property = "Sheet Name", Before = "PLAN", After = "FLOOR PLAN", Status = "Applied" } });
        Assert.Equal("FLOOR PLAN", Assert.Single(ChangeLog.Read()).After);
    }
}
