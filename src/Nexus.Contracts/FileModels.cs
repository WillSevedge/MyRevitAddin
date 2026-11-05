namespace Nexus.Contracts;

/// <summary>Create one PDF per sheet (Revit sheet, AutoCAD layout).</summary>
public sealed class ExportPdfRequest
{
    /// <summary>An open document (<see cref="DocumentInfo.Id"/>), for agents.</summary>
    public string? DocumentId { get; set; }
    /// <summary>The drawing, for AutoCAD's Core Console (the hub's copy of the saved file).</summary>
    public string Path { get; set; } = "";
    /// <summary>Sheets to export (<see cref="DataItem.Id"/>); empty for every sheet.</summary>
    public List<string> ItemIds { get; set; } = new();
    public string OutputFolder { get; set; } = "";
    /// <summary>Sheet number and name by item id, for file names (AutoCAD: the title block values the hub read).</summary>
    public Dictionary<string, string> Numbers { get; set; } = new();
    public Dictionary<string, string> Names { get; set; } = new();
    /// <summary>File name without extension; {Number}, {Name} and {File} are replaced.</summary>
    public string FileNamePattern { get; set; } = PdfNames.DefaultPattern;
}

public sealed class ExportPdfResult
{
    public List<ExportedSheet> Sheets { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long ElapsedMs { get; set; }
}

public sealed class ExportedSheet
{
    public string ItemId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The PDF, or null when <see cref="Error"/> says why there is none.</summary>
    public string? PdfPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Work for AutoCAD's Core Console (accoreconsole.exe, no user interface): PDFs of a drawing's layouts.
/// The hub writes this as JSON, starts the console on a copy of the saved drawing with NEXUS_JOB pointing at
/// it, and the NEXUSJOB command writes a <see cref="HeadlessJobResult"/> to <see cref="ResultPath"/>.
/// </summary>
public sealed class HeadlessJob
{
    public const string EnvironmentVariable = "NEXUS_JOB";
    public ExportPdfRequest? Pdf { get; set; }
    public string ResultPath { get; set; } = "";
}

public sealed class HeadlessJobResult
{
    public ExportPdfResult? Pdf { get; set; }
    public string? Error { get; set; }
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
}

public static class PdfNames
{
    public const string DefaultPattern = "{Number} - {Name}";

    /// <summary>The file name (no extension) for a sheet, safe for Windows.</summary>
    public static string For(string pattern, string number, string name, string file)
    {
        string text = (string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern)
            .Replace("{Number}", number).Replace("{Name}", name).Replace("{File}", file);
        // Windows' rules (the same on every machine): no <>:"/\|?* and no control characters.
        var chars = text.Select(c => c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c).ToArray();
        string clean = new string(chars).Trim().Trim('.', '-', ' ').Trim();
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        return clean.Length == 0 ? "Sheet" : clean.Length > 150 ? clean.Substring(0, 150).Trim() : clean;
    }

    /// <summary><paramref name="baseName"/>.pdf in <paramref name="folder"/>, numbered when taken in this export.</summary>
    public static string Unique(string folder, string baseName, ISet<string> taken)
    {
        string path = System.IO.Path.Combine(folder, baseName + ".pdf");
        for (int i = 2; taken.Contains(path); i++)
            path = System.IO.Path.Combine(folder, $"{baseName} ({i}).pdf");
        taken.Add(path);
        return path;
    }
}
