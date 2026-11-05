using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Health;

namespace Nexus.Tests;

public sealed class SheetHealthTests
{
    private static readonly HostInfo Revit = new() { HostKind = HostKinds.Revit, Product = "Revit", Version = "2026", ProcessId = 1, Features = { AgentFeatures.Write } };
    private static readonly HostInfo Acad = new() { HostKind = HostKinds.AutoCAD, Product = "AutoCAD", Version = "2026", ProcessId = 2, Features = { AgentFeatures.Write } };

    private static PropertyValue P(string name, string value, string id) => new()
    {
        Name = name, Id = id, Value = value, RawValue = value, Source = PropertySource.BuiltIn, StorageType = "String", OwnerId = "o",
    };

    private static DataItem Sheet(string number, string title, string drawnBy = "WS", string project = "2024-017") => new()
    {
        Id = "uid-" + number + title, ItemType = "Sheet", Name = $"{number} - {title}", Key = number,
        Groups =
        {
            new PropertyGroup("Sheet · Identity Data")
            {
                Properties =
                {
                    P("Sheet Number", number, "SHEET_NUMBER"), P("Sheet Name", title, "SHEET_NAME"),
                    P("Drawn By", drawnBy, "SHEET_DRAWN_BY"), P("Project Number", project, "PROJECT_NUMBER"),
                },
            },
        },
    };

    private static DataItem Layout(string dwgNo, string title, string project = "2024-017") => new()
    {
        Id = "h-" + dwgNo, ItemType = "Layout", Name = dwgNo, Key = dwgNo,
        Groups = { new PropertyGroup("Title Block") { Properties = { P("DWG_NO", dwgNo, "A1"), P("SHEET_TITLE", title, "A2"), P("PROJNO", project, "A3") } } },
    };

    private static List<HealthIssue> Check(params (HostInfo Host, string File, DataItem[] Items)[] files)
    {
        var table = ResultTable.Build(files.Select(f => new ResultSource
        {
            Host = f.Host, DocumentTitle = f.File,
            Result = new ReadResult { ReaderId = "sheets", DocumentId = f.File, Items = f.Items.ToList() },
        }), sheetFields: SheetFieldMap.Default());
        return SheetHealth.Check(table.Rows, (r, c) => r.Values.TryGetValue(c, out var v) ? v.Value : null, Editing.Blocker);
    }

    [Fact]
    public void Duplicate_numbers_are_found_across_programs()
    {
        var issues = Check(
            (Revit, "Tower.rvt", new[] { Sheet("A101", "FLOOR PLAN"), Sheet("A102", "ROOF PLAN") }),
            (Acad, "Site.dwg", new[] { Layout("A101", "SITE PLAN") }));

        var dups = issues.Where(i => i.RuleId == "number-duplicate").ToList();
        Assert.Equal(2, dups.Count);
        Assert.Contains(dups, i => i.Row.Document == "Tower.rvt" && i.Message.Contains("Site.dwg"));
        Assert.All(dups, i => Assert.Equal(HealthSeverity.Error, i.Severity));
        Assert.All(dups, i => Assert.False(i.CanFix)); // needs a person
    }

    [Fact]
    public void Template_placeholders_and_odd_project_numbers_are_flagged()
    {
        var issues = Check(
            (Revit, "Tower.rvt", new[] { Sheet("A101", "FLOOR PLAN", drawnBy: "Author"), Sheet("A102", "ROOF PLAN") }),
            // Project Number is per title block in AutoCAD (Revit's is project-wide).
            (Acad, "Civil.dwg", new[] { Layout("C101", "GRADING"), Layout("C102", "UTILITIES", project: "2024-071"), Layout("C103", "DETAILS") }));

        var placeholder = Assert.Single(issues, i => i.RuleId == "placeholder");
        Assert.Equal("A101", placeholder.Row.Key);
        Assert.Contains("Author", placeholder.Message);

        var project = Assert.Single(issues, i => i.RuleId == "project-number");
        Assert.Equal("C102", project.Row.Key);
        Assert.Equal("2024-017", project.FixValue);
    }

    [Fact]
    public void Pattern_capitals_and_spaces_get_one_click_fixes()
    {
        var issues = Check((Revit, "Tower.rvt", new[]
        {
            Sheet("A101", "FLOOR PLAN"), Sheet("A102", "ROOF PLAN"), Sheet("A103", "SECTIONS"),
            Sheet("A-104", "Details"), Sheet("A105", "WALL  SECTIONS "),
        }));

        var pattern = Assert.Single(issues, i => i.RuleId == "number-pattern");
        Assert.Equal("A104", pattern.FixValue);

        var caps = Assert.Single(issues, i => i.RuleId == "title-case");
        Assert.Equal("DETAILS", caps.FixValue);

        var spaces = issues.Where(i => i.RuleId == "spaces").ToList();
        Assert.Contains(spaces, i => i.FixValue == "WALL SECTIONS");
    }

    [Fact]
    public void A_pattern_fix_is_not_offered_when_it_would_create_a_duplicate()
    {
        var issues = Check((Revit, "Tower.rvt", new[]
        {
            Sheet("A101", "FLOOR PLAN"), Sheet("A102", "ROOF PLAN"), Sheet("A103", "SECTIONS"), Sheet("A-101", "DETAILS"),
        }));
        var pattern = Assert.Single(issues, i => i.RuleId == "number-pattern");
        Assert.Null(pattern.FixValue);
    }

    [Fact]
    public void A_clean_set_has_no_issues()
    {
        Assert.Empty(Check((Revit, "Tower.rvt", new[] { Sheet("A101", "FLOOR PLAN"), Sheet("A102", "ROOF PLAN") })));
    }
}
