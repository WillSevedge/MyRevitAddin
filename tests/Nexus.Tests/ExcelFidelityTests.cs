using Nexus.Hub.Core.Excel;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>
/// Runs the hub's real workbook write against a macro-enabled workbook full of features that
/// spreadsheet libraries drop, and checks that only the intended cells changed.
/// </summary>
[Collection("NexusHome")]
public sealed class ExcelFidelityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-fidelity-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ExcelFidelityTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXUS_HOME", null);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static ExcelLink Link(string path) => new()
    {
        WorkbookPath = path,
        Worksheet = TestWorkbooks.Sheet,
        HeaderRow = 1,
        KeyColumnId = "key",
        Columns =
        {
            new ExcelColumnMap { Header = "Sheet No.", ColumnId = "key" },
            new ExcelColumnMap { Header = "Sheet Title", ColumnId = "title" },
            new ExcelColumnMap { Header = "Rev", ColumnId = "rev" },
        },
    };

    [Fact]
    public void Writing_cells_keeps_everything_else_in_the_workbook()
    {
        string path = Path.Combine(_dir, "Sheet Index.xlsm");
        TestWorkbooks.WriteFeatureRich(path);
        var before = WorkbookSnapshot.Take(path);

        // C-201's title (row 4, column B) and A-102's revision (row 3, column C).
        ExcelWorkbook.Write(Link(path), new[] { new ExcelCellUpdate(4, 2, "Grading and Drainage"), new ExcelCellUpdate(3, 3, "3") },
            Array.Empty<IReadOnlyDictionary<string, string>>());

        Keep(path, "cell-edits");
        var after = WorkbookSnapshot.Take(path);
        var expected = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [TestWorkbooks.Sheet] = new Dictionary<string, string> { ["B4"] = "Grading and Drainage", ["C3"] = "3" },
        };
        var issues = WorkbookSnapshot.Compare(before, after, expected, ExcelWorkbook.PartsAWriteMayChange(before, TestWorkbooks.Sheet));
        Report("Cell edits", issues);
        Assert.Empty(issues);
    }

    [Fact]
    public void New_rows_fill_the_prebuilt_blank_rows_without_inserting()
    {
        string path = Path.Combine(_dir, "Sheet Index.xlsm");
        TestWorkbooks.WriteFeatureRich(path);
        var before = WorkbookSnapshot.Take(path);

        ExcelWorkbook.Write(Link(path), Array.Empty<ExcelCellUpdate>(), new[]
        {
            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["Sheet No."] = "M-101", ["Sheet Title"] = "Mechanical Plan", ["Rev"] = "1" },
        });

        var after = WorkbookSnapshot.Take(path);
        var expected = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [TestWorkbooks.Sheet] = new Dictionary<string, string> { ["A5"] = "M-101", ["B5"] = "Mechanical Plan", ["C5"] = "1" },
        };
        var issues = WorkbookSnapshot.Compare(before, after, expected, ExcelWorkbook.PartsAWriteMayChange(before, TestWorkbooks.Sheet));
        Report("New row", issues);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_refused_write_leaves_the_file_untouched()
    {
        string path = Path.Combine(_dir, "Sheet Index.xlsm");
        TestWorkbooks.WriteFeatureRich(path);
        byte[] original = File.ReadAllBytes(path);

        // E2 holds a formula: the whole write is refused, including the valid edit before it.
        var ex = Assert.Throws<InvalidOperationException>(() => ExcelWorkbook.Write(Link(path),
            new[] { new ExcelCellUpdate(2, 2, "Cover Sheet"), new ExcelCellUpdate(2, 5, "99") },
            Array.Empty<IReadOnlyDictionary<string, string>>()));
        Assert.Contains("formula", ex.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Dates_numbers_and_codes_keep_their_types()
    {
        string path = Path.Combine(_dir, "Sheet Index.xlsm");
        TestWorkbooks.WriteFeatureRich(path);

        var date = new DateTime(2026, 3, 15);
        ExcelWorkbook.Write(Link(path), new[]
        {
            new ExcelCellUpdate(2, 6, date.ToString("d", System.Globalization.CultureInfo.CurrentCulture)), // date-formatted cell
            new ExcelCellUpdate(2, 3, "4"),     // number cell
            new ExcelCellUpdate(3, 1, "007"),   // code with leading zeros stays text
        }, Array.Empty<IReadOnlyDictionary<string, string>>());

        var cells = WorkbookSnapshot.Take(path).Sheets[TestWorkbooks.Sheet].Cells;
        Assert.Equal(date.ToOADate().ToString("R", System.Globalization.CultureInfo.InvariantCulture), cells["F2"].Value);
        Assert.Equal("2", cells["F2"].Style); // still shown as a date
        Assert.Equal("4", cells["C2"].Value);
        Assert.Equal("007", cells["A3"].Value);
    }

    /// <summary>Set NEXUS_FIDELITY_KEEP to a folder to keep the written workbook for inspection.</summary>
    private static void Keep(string path, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("NEXUS_FIDELITY_KEEP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, name + Path.GetExtension(path)), true);
        TestWorkbooks.WriteFeatureRich(Path.Combine(dir, "original" + Path.GetExtension(path)));
    }

    private void Report(string title, List<FidelityIssue> issues)
    {
        _output.WriteLine($"{title}: {issues.Count} issue(s)");
        foreach (var i in issues.OrderBy(i => i.Severity)) _output.WriteLine("  " + i);
    }
}
