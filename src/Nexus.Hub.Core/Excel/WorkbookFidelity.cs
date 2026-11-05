using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Nexus.Hub.Core.Excel;

/// <summary>
/// An inventory of a workbook package (.xlsx/.xlsm), read straight from the zip with no Excel library:
/// every part with its content type and hash, and per worksheet the features that libraries are known to
/// drop (form controls, comments, data validation, conditional formatting, x14 extensions, tables...) plus
/// every cell's formula, value and style. Comparing a snapshot taken before a write with one taken after
/// proves the write changed only the cells it meant to.
/// </summary>
public sealed class WorkbookSnapshot
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Worksheet elements (other than the cells) that must survive a write unchanged.</summary>
    private static readonly string[] SheetFeatures =
    {
        "sheetPr", "sheetProtection", "protectedRanges", "autoFilter", "sortState", "mergeCells", "phoneticPr",
        "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup",
        "headerFooter", "rowBreaks", "colBreaks", "customProperties", "drawing", "legacyDrawing",
        "legacyDrawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst", "cols",
    };

    /// <summary>Part name (e.g. "/xl/vbaProject.bin") → content type and SHA-256.</summary>
    public Dictionary<string, (string ContentType, string Hash)> Parts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Worksheet name → its features and cells.</summary>
    public Dictionary<string, WorksheetSnapshot> Sheets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workbook-level XML that must survive: defined names, sheet list, workbook properties (codeName for VBA).</summary>
    public Dictionary<string, string> WorkbookFeatures { get; } = new(StringComparer.Ordinal);

    public string? WorkbookPart { get; private set; }
    public string? SharedStringsPart { get; private set; }

    public static WorkbookSnapshot Take(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var snap = new WorkbookSnapshot();

        var types = Load(zip, "/[Content_Types].xml") ?? throw new InvalidDataException("Not an Office Open XML package.");
        var defaults = types.Root!.Elements(Ct + "Default").ToDictionary(e => (string)e.Attribute("Extension")!, e => (string)e.Attribute("ContentType")!, StringComparer.OrdinalIgnoreCase);
        var overrides = types.Root!.Elements(Ct + "Override").ToDictionary(e => (string)e.Attribute("PartName")!, e => (string)e.Attribute("ContentType")!, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string name = "/" + entry.FullName;
            if (name == "/[Content_Types].xml") continue;
            string ext = Path.GetExtension(name).TrimStart('.');
            string type = overrides.TryGetValue(name, out var o) ? o : defaults.TryGetValue(ext, out var d) ? d : "";
            using var s = entry.Open();
            snap.Parts[name] = (type, Convert.ToHexString(SHA256.HashData(s)));
        }

        // Workbook part from the package relationships.
        var rootRels = Load(zip, "/_rels/.rels");
        string? workbook = rootRels?.Root!.Elements(PkgRel + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true)
            .Select(r => Resolve("/", (string)r.Attribute("Target")!)).FirstOrDefault();
        if (workbook is null) return snap;
        snap.WorkbookPart = workbook;

        var wb = Load(zip, workbook)!;
        var wbRels = Relationships(zip, workbook);
        foreach (var name in new[] { "workbookPr", "definedNames", "sheets", "workbookProtection", "extLst" })
            snap.WorkbookFeatures[name] = Canonical(wb.Root!.Element(Main + name));

        snap.SharedStringsPart = wbRels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
        var shared = snap.SharedStringsPart is null ? new List<string>() : SharedStrings(Load(zip, snap.SharedStringsPart));

        foreach (var sheet in wb.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? Enumerable.Empty<XElement>())
        {
            string? rid = (string?)sheet.Attribute(Rel + "id");
            if (rid is null || !wbRels.TryGetValue(rid, out var rel)) continue;
            var xml = Load(zip, rel.Target);
            if (xml?.Root is null || xml.Root.Name != Main + "worksheet") continue;
            var ss = new WorksheetSnapshot { PartName = rel.Target };
            foreach (var f in SheetFeatures)
            {
                var all = xml.Root.Elements(Main + f).ToList();
                if (all.Count > 0) ss.Features[f] = string.Join("\n", all.Select(Canonical));
            }
            // Form and ActiveX controls are wrapped in mc:AlternateContent.
            var alternate = xml.Root.Elements(Mc + "AlternateContent").ToList();
            if (alternate.Count > 0) ss.Features["mc:AlternateContent"] = string.Join("\n", alternate.Select(Canonical));
            ss.Features["relationships"] = string.Join("\n", Relationships(zip, rel.Target).Values
                .Select(r => $"{r.Type} -> {r.Target}").OrderBy(x => x, StringComparer.Ordinal));

            foreach (var c in xml.Root.Element(Main + "sheetData")?.Descendants(Main + "c") ?? Enumerable.Empty<XElement>())
            {
                string? reference = (string?)c.Attribute("r");
                if (reference is null) continue;
                ss.Cells[reference] = new CellSnapshot(
                    (string?)c.Element(Main + "f"),
                    CellValue(c, shared),
                    (string?)c.Attribute("s") ?? "0");
            }
            snap.Sheets[(string)sheet.Attribute("name")!] = ss;
        }
        return snap;
    }

    /// <summary>
    /// Everything that differs between <paramref name="before"/> and <paramref name="after"/>, apart from
    /// <paramref name="expected"/> (sheet → cell reference → new value). Parts in <paramref name="mayChange"/>
    /// may be rewritten as long as their content checks pass; every other part must be byte-identical.
    /// </summary>
    /// <remarks><paramref name="newFormulas"/>: per sheet, cells allowed to appear with a formula (a table's calculated
    /// column copied into rows it was extended over).</remarks>
    public static List<FidelityIssue> Compare(WorkbookSnapshot before, WorkbookSnapshot after,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> expected, ISet<string>? mayChange = null,
        IReadOnlyDictionary<string, ISet<string>>? newFormulas = null)
    {
        var issues = new List<FidelityIssue>();
        mayChange ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, (type, hash)) in before.Parts)
        {
            if (!after.Parts.TryGetValue(name, out var now))
            {
                // calcChain is a cache Excel rebuilds; dropping it is allowed.
                if (!name.EndsWith("/calcChain.xml", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new FidelityIssue(FidelitySeverity.Lost, name, $"Part removed ({Short(type)})."));
                continue;
            }
            if (now.ContentType != type)
                issues.Add(new FidelityIssue(FidelitySeverity.Changed, name, $"Content type changed from {Short(type)} to {Short(now.ContentType)}."));
            else if (now.Hash != hash && !mayChange.Contains(name))
                issues.Add(new FidelityIssue(FidelitySeverity.Rewritten, name, $"Part rewritten ({Short(type)})."));
        }
        foreach (var name in after.Parts.Keys.Where(n => !before.Parts.ContainsKey(n)))
            if (!mayChange.Contains(name))
                issues.Add(new FidelityIssue(FidelitySeverity.Rewritten, name, "Part added."));

        foreach (var (key, xml) in before.WorkbookFeatures)
            if (!string.Equals(xml, after.WorkbookFeatures.GetValueOrDefault(key) ?? "", StringComparison.Ordinal))
                issues.Add(new FidelityIssue(xml.Length > 0 && (after.WorkbookFeatures.GetValueOrDefault(key) ?? "").Length == 0 ? FidelitySeverity.Lost : FidelitySeverity.Changed,
                    before.WorkbookPart ?? "workbook", $"Workbook <{key}> {(after.WorkbookFeatures.GetValueOrDefault(key) is { Length: > 0 } ? "changed" : "removed")}."));

        bool stylesKept = StylesUnchanged(before, after);
        foreach (var (sheetName, b) in before.Sheets)
        {
            if (!after.Sheets.TryGetValue(sheetName, out var a))
            {
                issues.Add(new FidelityIssue(FidelitySeverity.Lost, sheetName, "Worksheet removed."));
                continue;
            }
            foreach (var (feature, xml) in b.Features)
            {
                string now = a.Features.GetValueOrDefault(feature) ?? "";
                if (string.Equals(xml, now, StringComparison.Ordinal)) continue;
                issues.Add(new FidelityIssue(now.Length == 0 ? FidelitySeverity.Lost : FidelitySeverity.Changed, sheetName,
                    $"<{feature}> {(now.Length == 0 ? "removed" : "changed")}."));
            }
            foreach (var feature in a.Features.Keys.Where(f => !b.Features.ContainsKey(f)))
                issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"<{feature}> added."));

            var wanted = expected.GetValueOrDefault(sheetName) ?? new Dictionary<string, string>();
            var grown = newFormulas?.GetValueOrDefault(sheetName);
            foreach (var (cell, was) in b.Cells)
            {
                if (wanted.ContainsKey(cell)) continue;
                var now = a.Cells.GetValueOrDefault(cell);
                // An empty (formatted) cell that received the table's calculated column formula.
                if (grown?.Contains(cell) == true && was.Formula is null && was.Value.Length == 0 && now?.Formula is not null) continue;
                // A blank cell that disappears (or appears) is not a change.
                if (now is null) { if (was.Formula is not null || was.Value.Length > 0) issues.Add(new FidelityIssue(FidelitySeverity.Lost, sheetName, $"{cell}: cell removed (was '{was.Value}'{(was.Formula is null ? "" : $", ={was.Formula}")}).")); continue; }
                if (was.Formula != now.Formula)
                    issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"{cell}: formula changed from ={was.Formula} to ={now.Formula}."));
                else if (was.Formula is null && was.Value != now.Value)
                    issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"{cell}: value changed from '{was.Value}' to '{now.Value}'."));
                if (was.Style != now.Style && stylesKept)
                    issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"{cell}: style changed."));
            }
            foreach (var (cell, now) in a.Cells.Where(c => !b.Cells.ContainsKey(c.Key) && !wanted.ContainsKey(c.Key)))
                if ((now.Formula is not null || now.Value.Length > 0) && !(now.Formula is not null && grown?.Contains(cell) == true))
                    issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"{cell}: unexpected new value '{now.Value}'."));
            foreach (var (cell, value) in wanted)
            {
                var now = a.Cells.GetValueOrDefault(cell);
                if (!string.Equals(now?.Value ?? "", value, StringComparison.Ordinal))
                    issues.Add(new FidelityIssue(FidelitySeverity.Changed, sheetName, $"{cell}: expected '{value}' but found '{now?.Value ?? ""}'."));
            }
        }
        return issues;
    }

    /// <summary>Style indexes are only comparable when the styles part was not rewritten.</summary>
    private static bool StylesUnchanged(WorkbookSnapshot x, WorkbookSnapshot y) =>
        x.Parts.Where(p => p.Value.ContentType.EndsWith("styles+xml", StringComparison.Ordinal))
            .All(p => y.Parts.TryGetValue(p.Key, out var q) && q.Hash == p.Value.Hash);

    // ------------------------------------------------------------------ helpers

    private static string CellValue(XElement c, List<string> shared)
    {
        string? t = (string?)c.Attribute("t");
        if (t == "inlineStr") return string.Concat(c.Element(Main + "is")?.Descendants(Main + "t").Select(x => x.Value) ?? Enumerable.Empty<string>());
        string v = (string?)c.Element(Main + "v") ?? "";
        if (t == "s" && int.TryParse(v, out int i)) return i >= 0 && i < shared.Count ? shared[i] : "";
        return v;
    }

    private static List<string> SharedStrings(XDocument? xml) =>
        xml?.Root?.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t")
            .Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value))).ToList() ?? new List<string>();

    private static Dictionary<string, (string Type, string Target)> Relationships(ZipArchive zip, string partName)
    {
        string dir = partName[..(partName.LastIndexOf('/') + 1)];
        string relsName = dir + "_rels/" + partName[(partName.LastIndexOf('/') + 1)..] + ".rels";
        var xml = Load(zip, relsName);
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var r in xml?.Root?.Elements(PkgRel + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            string target = (string)r.Attribute("Target")!;
            bool external = (string?)r.Attribute("TargetMode") == "External";
            map[(string)r.Attribute("Id")!] = ((string)r.Attribute("Type")!, external ? target : Resolve(dir, target));
        }
        return map;
    }

    private static string Resolve(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target;
        var parts = new List<string>(baseDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return "/" + string.Join("/", parts);
    }

    private static XDocument? Load(ZipArchive zip, string partName)
    {
        var entry = zip.GetEntry(partName.TrimStart('/'));
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Element XML with attributes sorted, so equal content compares equal whoever wrote it.</summary>
    private static string Canonical(XElement? e)
    {
        if (e is null) return "";
        var copy = new XElement(e.Name,
            e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
            e.Nodes().Select(n => n is XElement child ? XElement.Parse(Canonical(child)) : n));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static string Short(string contentType) =>
        contentType.Length == 0 ? "unknown type" : contentType[(contentType.LastIndexOf('/') + 1)..];
}

public sealed class WorksheetSnapshot
{
    public string PartName { get; init; } = "";
    public Dictionary<string, string> Features { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CellSnapshot> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CellSnapshot(string? Formula, string Value, string Style);

public enum FidelitySeverity
{
    /// <summary>A part, feature or value is gone.</summary>
    Lost,
    /// <summary>Content differs.</summary>
    Changed,
    /// <summary>Re-serialized; content may be equivalent but was not left untouched.</summary>
    Rewritten,
}

public sealed record FidelityIssue(FidelitySeverity Severity, string Where, string What)
{
    public override string ToString() => $"{Severity}: {Where}: {What}";
}
