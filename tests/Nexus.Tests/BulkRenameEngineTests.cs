using Nexus.Rename;

namespace Nexus.Tests;

public sealed class BulkRenameEngineTests
{
    private static string Run(RenameRules rules, string name, int number = 1, Func<string, string?>? tokens = null) =>
        RenameEngine.Apply(rules, name, number, tokens);

    [Fact]
    public void Regex_replaces_with_groups()
    {
        var r = new RenameRules { Regex = { Enabled = true, Match = @"^Level (\d+)$", Replace = "L$1" } };
        Assert.Equal("L02", Run(r, "Level 02"));
        Assert.Equal("Roof", Run(r, "Roof"));
    }

    [Fact]
    public void Replace_handles_pairs_case_and_whole_words()
    {
        var r = new RenameRules { Replace = { Enabled = true, Find = "Plan|Lvl", With = "PLAN|Level" } };
        Assert.Equal("Level 1 PLAN", Run(r, "lvl 1 plan"));

        r.Replace = new ReplaceRule { Enabled = true, Find = "A", With = "B", WholeWord = true };
        Assert.Equal("B-101 Area", Run(r, "A-101 Area"));

        r.Replace = new ReplaceRule { Enabled = true, Find = "-", With = "", FirstOnly = true };
        Assert.Equal("M101-A", Run(r, "M-101-A"));
    }

    [Fact]
    public void Case_with_exceptions()
    {
        var r = new RenameRules { Case = { Enabled = true, Mode = CaseMode.Title, Exceptions = "HVAC, 3D" } };
        Assert.Equal("Level 1 HVAC Plan - 3D", Run(r, "LEVEL 1 hvac PLAN - 3d"));
        Assert.Equal("Owner's 2nd Floor", Run(r, "OWNER'S 2ND FLOOR"));
        r.Case = new CaseRule { Enabled = true, Mode = CaseMode.Sentence };
        Assert.Equal("01 - First floor plan", Run(r, "01 - FIRST FLOOR PLAN"));
        r.Case = new CaseRule { Enabled = true, Mode = CaseMode.Upper };
        Assert.Equal("SECTION A", Run(r, "Section a"));
    }

    [Fact]
    public void Remove_options()
    {
        var r = new RenameRules { Remove = { Enabled = true, First = 2, Last = 1 } };
        Assert.Equal("Plan", Run(r, "1 Plan2"));
        r.Remove = new RemoveRule { Enabled = true, From = 3, To = 5 };
        Assert.Equal("ABFG", Run(r, "ABCDEFG"));
        r.Remove = new RemoveRule { Enabled = true, Crop = CropMode.Before, CropText = "Plan" };
        Assert.Equal("Plan - Level 1", Run(r, "Copy of Plan - Level 1"));
        r.Remove = new RemoveRule { Enabled = true, Crop = CropMode.After, CropText = "Level" };
        Assert.Equal("Plan - Level", Run(r, "Plan - Level 1"));
        r.Remove = new RemoveRule { Enabled = true, Words = "Copy, of", Trim = true };
        Assert.Equal("Plan 1", Run(r, "Copy of Plan  1"));
        r.Remove = new RemoveRule { Enabled = true, Digits = true, Symbols = true, Accents = true, DoubleSpaces = true, Trim = true };
        Assert.Equal("Cafe Plan", Run(r, " Café (Plan) 2 "));
    }

    [Fact]
    public void Add_prefix_insert_suffix_and_word_space()
    {
        var r = new RenameRules { Add = { Enabled = true, Prefix = "{Level} - ", Insert = "_", AtPosition = -1, Suffix = " (new)", WordSpace = true } };
        string? Tokens(string t) => t == "Level" ? "L1" : null;
        Assert.Equal("L1 - Floor Plan_ (new)", Run(r, "FloorPlan", tokens: Tokens));
        r.Add = new AddRule { Enabled = true, Insert = "-", AtPosition = 2 };
        Assert.Equal("A-101", Run(r, "A101"));
    }

    [Fact]
    public void Numbering_styles_and_modes()
    {
        var r = new RenameRules { Numbering = { Enabled = true, Mode = NumberingMode.Suffix, Start = 1, Increment = 1, Pad = 3, Separator = "-" } };
        Assert.Equal("Detail-003", Run(r, "Detail", 3));
        r.Numbering.Mode = NumberingMode.Prefix;
        r.Numbering.Style = NumberingStyle.UpperLetters;
        Assert.Equal("AA-Section", Run(r, "Section", 27));
        r.Numbering = new NumberingRule { Enabled = true, Mode = NumberingMode.Replace, Start = 101, Increment = 1, Pad = 1 };
        Assert.Equal("102", Run(r, "anything", 2));
        Assert.Equal("Z", RenameEngine.FormatNumber(26, NumberingStyle.UpperLetters, 0));
        Assert.Equal("ab", RenameEngine.FormatNumber(28, NumberingStyle.LowerLetters, 0));
    }

    [Fact]
    public void Name_fixed_with_tokens_reports_missing()
    {
        var r = new RenameRules { Name = { Enabled = true, Mode = NameMode.Fixed, Fixed = "{Level} {View Type} {Nope}" } };
        var missing = new List<string>();
        string result = RenameEngine.Apply(r, "x", 1, t => t switch { "Level" => "L2", "View Type" => "Floor Plan", _ => null }, missing);
        Assert.Equal("L2 Floor Plan ", result);
        Assert.Equal(new[] { "Nope" }, missing);
    }

    [Fact]
    public void Filter_wildcards_regex_and_invert()
    {
        Assert.True(RenameEngine.Passes(new FilterRule { Enabled = true, Mask = "Level*" }, "Level 1"));
        Assert.False(RenameEngine.Passes(new FilterRule { Enabled = true, Mask = "Level*" }, "Roof"));
        Assert.True(RenameEngine.Passes(new FilterRule { Enabled = true, Mask = "Roof;Level ?" }, "Level 2"));
        Assert.True(RenameEngine.Passes(new FilterRule { Enabled = true, Mask = @"^\d", UseRegex = true }, "1 - Plan"));
        Assert.True(RenameEngine.Passes(new FilterRule { Enabled = true, Mask = "Level*", Invert = true }, "Roof"));
        Assert.True(RenameEngine.Passes(new FilterRule { Enabled = false, Mask = "nothing" }, "Roof"));
    }

    private static RenameItem Item(string name, string? scope = "view", string forbidden = "\\:{}[]|;<>?`~") =>
        new() { Key = name, Current = name, Kind = "View", Scope = scope, ForbiddenChars = forbidden };

    [Fact]
    public void Plan_numbers_in_order_and_restarts_per_group()
    {
        var levels = new Dictionary<string, string> { ["a"] = "L1", ["b"] = "L1", ["c"] = "L2" };
        var items = levels.Keys.Select(k => new RenameItem { Key = k, Current = k, Scope = "view", Tokens = t => t == "Level" ? levels[k] : null }).ToList();
        var rules = new RenameRules
        {
            Name = { Enabled = true, Mode = NameMode.Fixed, Fixed = "{Level} Detail" },
            Numbering = { Enabled = true, Mode = NumberingMode.Suffix, Pad = 1, Separator = " ", RestartFor = "{Level}" },
        };
        var plan = RenamePlanner.Plan(items, rules);
        Assert.Equal(new[] { "L1 Detail 1", "L1 Detail 2", "L2 Detail 1" }, plan.Select(p => p.New));
        Assert.All(plan, p => Assert.Equal(RenameStatus.Rename, p.Status));
    }

    [Fact]
    public void Plan_flags_duplicates_invalid_empty_locked_and_skipped()
    {
        var items = new List<RenameItem>
        {
            Item("Plan 1"), Item("Plan 2"), Item("Keep me"),
            new() { Key = "locked", Current = "Locked view", Scope = "view", Locked = "Owned by Pat" },
            Item("Other"),
        };
        // Everything becomes "Keep me"; "Other" is skipped.
        var rules = new RenameRules { Name = { Enabled = true, Mode = NameMode.Fixed, Fixed = "Keep me" } };
        var plan = RenamePlanner.Plan(items, rules, i => i.Current != "Other");
        Assert.Equal(RenameStatus.Duplicate, plan[0].Status);
        Assert.Equal(RenameStatus.Duplicate, plan[1].Status);
        Assert.Equal(RenameStatus.Unchanged, plan[2].Status);
        Assert.Equal(RenameStatus.Locked, plan[3].Status);
        Assert.Equal(RenameStatus.Skipped, plan[4].Status);

        rules.Name.Fixed = "Bad: name?";
        Assert.Equal(RenameStatus.Invalid, RenamePlanner.Plan(new[] { Item("A") }, rules)[0].Status);
        rules.Name.Fixed = "  ";
        Assert.Equal(RenameStatus.Empty, RenamePlanner.Plan(new[] { Item("A") }, rules)[0].Status);
        // No scope: duplicates allowed (room names).
        rules.Name.Fixed = "Office";
        Assert.All(RenamePlanner.Plan(new[] { Item("A", null), Item("B", null) }, rules), p => Assert.Equal(RenameStatus.Rename, p.Status));
        // Names in use outside the list.
        var outside = RenamePlanner.Plan(new[] { Item("A") }, rules, others: new[] { ("view", "office") });
        Assert.Equal(RenameStatus.Duplicate, outside[0].Status);
    }

    [Fact]
    public void Swaps_are_allowed_and_need_a_temporary_name()
    {
        var items = new[] { Item("A101"), Item("A102") };
        var rules = new RenameRules { Replace = { Enabled = true, Find = "A101|A102", With = "A102|A101" } };
        var plan = RenamePlanner.Plan(items, rules);
        Assert.All(plan, p => Assert.Equal(RenameStatus.Rename, p.Status));
        var (direct, via) = RenamePlanner.Order(plan);
        Assert.Empty(direct);
        Assert.Equal(2, via.Count);

        // Shift into a free name: no temporary needed.
        var shift = RenamePlanner.Plan(new[] { Item("A102") }, new RenameRules { Replace = { Enabled = true, Find = "102", With = "103" } });
        Assert.Single(RenamePlanner.Order(shift).Direct);
    }

    [Fact]
    public void Bad_regex_is_reported_not_thrown()
    {
        var plan = RenamePlanner.Plan(new[] { Item("A") }, new RenameRules { Regex = { Enabled = true, Match = "(" } });
        Assert.Equal(RenameStatus.Invalid, plan[0].Status);
    }

    [Fact]
    public void Presets_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nexus-presets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var presets = new RenamePresets(dir);
            var rules = new RenameRules { Case = { Enabled = true, Mode = CaseMode.Upper }, Numbering = { Enabled = true, Pad = 3 } };
            presets.Save("Views: upper", rules);
            Assert.Equal(new[] { "Views_ upper" }, presets.Names());
            var loaded = presets.Load("Views: upper")!;
            Assert.Equal(CaseMode.Upper, loaded.Case.Mode);
            Assert.Equal(3, loaded.Numbering.Pad);
            presets.Delete("Views: upper");
            Assert.Empty(presets.Names());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignored */ }
        }
    }
}
