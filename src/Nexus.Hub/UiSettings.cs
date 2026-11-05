using System.IO;
using System.Text.Json;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>Per-user hub preferences: last dataset, visible columns per dataset, active Excel link.</summary>
internal sealed class UiSettings
{
    private static string FilePath => Path.Combine(NexusPaths.Root, "hub-ui.json");

    public string? LastDataset { get; set; }
    public Dictionary<string, List<string>> VisibleColumns { get; set; } = new();
    public string? ExcelWorkbook { get; set; }
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string? Theme { get; set; }
    /// <summary>Print &amp; PDF: last output folder and file name pattern.</summary>
    public string? PdfFolder { get; set; }
    public string? PdfNamePattern { get; set; }
    /// <summary>Shell: the workspace last shown (Home, Data, Connections) and the navigation rail state.</summary>
    public string? Workspace { get; set; }
    public bool RailExpanded { get; set; }
    /// <summary>Table rows: "Compact" or "Comfortable" (default).</summary>
    public string? Density { get; set; }
    /// <summary>The inspector (details pane) stays open from one session to the next.</summary>
    public bool InspectorPinned { get; set; }
    /// <summary>Markups: the PDFs (Bluebeam sets) last read.</summary>
    public List<string> MarkupPdfs { get; set; } = new();
    /// <summary>Sheet index: the Revit model that holds the drawing index, the shared parameter its schedules sort by,
    /// and whether new dummy sheets get no title block.</summary>
    public string? SheetIndexDocument { get; set; }
    public string? SheetIndexDiscipline { get; set; }
    public bool SheetIndexNoTitleBlock { get; set; } = true;

    /// <summary>
    /// Version of the default Sheets columns. When the defaults change, the saved column choice for
    /// Sheets is dropped once so the new defaults show.
    /// </summary>
    public int SheetColumnsVersion { get; set; }
    private const int CurrentSheetColumnsVersion = 2;

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json.Options) ?? new UiSettings();
                if (settings.SheetColumnsVersion < CurrentSheetColumnsVersion)
                {
                    // v2: Revit sheets open on the Properties palette parameters, not a column per revision.
                    settings.VisibleColumns.Remove(ViewModels.MainViewModel.SheetsDatasetId);
                    settings.SheetColumnsVersion = CurrentSheetColumnsVersion;
                }
                return settings;
            }
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not read the hub settings; using defaults.", ex);
        }
        return new UiSettings { SheetColumnsVersion = CurrentSheetColumnsVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(NexusPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception ex)
        {
            HubLog.Warn("Could not save the hub settings.", ex);
        }
    }
}
