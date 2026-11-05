using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Tests;

public sealed class SheetIndexSyncTests
{
    private const string Arch = "Arch.rvt";
    private const string Discipline = "Index Discipline";

    private static DataItem RevitSheet(string number, string name, string rev, bool listed, string discipline, bool placeholder = false) => new()
    {
        Id = number, ItemType = placeholder ? "Placeholder Sheet" : "Sheet", Name = $"{number} - {name}", Key = number,
        Groups =
        {
            new PropertyGroup("Sheet · Identity Data")
            {
                Properties =
                {
                    new PropertyValue { Name = "Sheet Number", Id = "SHEET_NUMBER", Value = number },
                    new PropertyValue { Name = "Sheet Name", Id = "SHEET_NAME", Value = name },
                    new PropertyValue { Name = "Appears In Sheet List", Id = SheetIndexSync.SheetListId, Value = listed ? "Yes" : "No", RawValue = listed ? "1" : "0", StorageType = "Integer" },
                    new PropertyValue { Name = Discipline, Id = "guid-1", Value = discipline, Source = PropertySource.Shared, StorageType = "String" },
                },
            },
            new PropertyGroup("Current Revision") { Properties = { new PropertyValue { Name = "Revision Number", Value = rev, Source = PropertySource.Derived } } },
        },
    };

    private static ResultSource Revit(params DataItem[] sheets) => new()
    {
        Host = new HostInfo { HostKind = HostKinds.Revit, Product = "Revit" }, DocumentTitle = Arch,
        Result = new ReadResult { ReaderId = "revit.sheets", Items = sheets.ToList() },
    };

    private static ResultSource Dwg(string file, string number, string title, string rev) => new()
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
                    Groups =
                    {
                        new PropertyGroup("Title Block")
                        {
                            Properties =
                            {
                                new PropertyValue { Name = "DWGNO", Id = "DWGNO", Value = number },
                                new PropertyValue { Name = "TITLE", Id = "TITLE", Value = title },
                                new PropertyValue { Name = "REV", Id = "REV", Value = rev },
                            },
                        },
                    },
                },
            },
        },
    };

    private static List<IndexSyncRow> Plan(params ResultSource[] sources) =>
        SheetIndexSync.Plan(ResultTable.Build(sources, sheetFields: SheetFieldMap.Default()), Arch, Discipline);

    [Fact]
    public void Each_drawing_gets_what_its_dummy_sheet_needs()
    {
        var plan = Plan(
            Revit(
                RevitSheet("E-100", "ELECTRICAL LEGEND", "B", true, "ELECTRICAL"),
                RevitSheet("E-101", "POWER PLAN", "", true, "ELECTRICAL", placeholder: true),
                RevitSheet("E-102", "OLD NAME", "A", false, ""),
                RevitSheet("C-101", "SITE PLAN", "B", true, "CIVIL")),
            Dwg("E-101.dwg", "E-101", "POWER PLAN", "B"),
            Dwg("E-102.dwg", "E-102", "LIGHTING PLAN", "B"),
            Dwg("E-103.dwg", "E-103", "GROUNDING PLAN", "-"),
            Dwg("C-101.dwg", "C-101", "SITE PLAN", "B"));
        var byNumber = plan.ToDictionary(r => r.Number);

        Assert.Equal(new[] { IndexAction.MakeSheet, IndexAction.SetRevision }, byNumber["E-101"].Actions);
        Assert.Equal(new[] { IndexAction.Rename, IndexAction.SetRevision, IndexAction.ShowInList, IndexAction.SetDiscipline }, byNumber["E-102"].Actions);
        Assert.Equal("ELECTRICAL", byNumber["E-102"].NewDiscipline);
        Assert.Equal(new[] { IndexAction.Create }, byNumber["E-103"].Actions);
        Assert.Equal("", byNumber["E-103"].DrawingRev); // "-" is no revision yet
        Assert.True(byNumber["C-101"].InSync);
        Assert.True(plan.Last().InSync); // in-sync rows sort last
    }

    [Fact]
    public void Requests_carry_only_what_is_needed()
    {
        var plan = Plan(
            Revit(RevitSheet("E-100", "LEGEND", "B", true, "ELECTRICAL"), RevitSheet("E-101", "POWER PLAN", "", true, "ELECTRICAL", placeholder: true)),
            Dwg("E-101.dwg", "E-101", "POWER PLAN", "B"),
            Dwg("E-103.dwg", "E-103", "GROUNDING PLAN", "A"));
        var byNumber = plan.ToDictionary(r => r.Number);

        var convert = SheetIndexSync.ToRequest(byNumber["E-101"], Discipline);
        Assert.True(convert.Update);
        Assert.True(convert.ReplacePlaceholder);
        Assert.Equal("B", convert.Revision);
        Assert.Equal("", convert.Name);           // the name already matches
        Assert.Null(convert.ShowInSheetList);    // already in the list
        Assert.Empty(convert.Values);

        var create = SheetIndexSync.ToRequest(byNumber["E-103"], Discipline);
        Assert.Equal(("GROUNDING PLAN", "A", true), (create.Name, create.Revision, create.ShowInSheetList));
        Assert.Equal("ELECTRICAL", create.Values[Discipline]);
        Assert.False(create.ReplacePlaceholder);
    }

    [Fact]
    public void A_drawing_without_a_number_is_matched_by_file_name_and_default_layout_names_are_ignored()
    {
        var plan = Plan(
            Revit(RevitSheet("M-201", "PIPING PLAN", "A", true, "MECHANICAL")),
            Dwg("M-201 - PIPING PLAN.dwg", "", "Layout1", "A"));
        var row = Assert.Single(plan);
        Assert.True(row.MatchedByFile);
        Assert.Equal("M-201", row.Number);
        Assert.Equal("", row.DrawingName); // "Layout1" is not a title: no rename
        Assert.True(row.InSync);
    }

    [Fact]
    public void Discipline_parameters_are_the_index_models_shared_text_parameters() =>
        Assert.Equal(new[] { Discipline },
            SheetIndexSync.DisciplineParameters(ResultTable.Build(new[] { Revit(RevitSheet("A-101", "PLAN", "", true, "ARCH")) }, sheetFields: SheetFieldMap.Default()), Arch));
}
