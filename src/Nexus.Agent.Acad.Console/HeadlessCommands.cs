using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Nexus.Agent.Acad.HeadlessCommands))]

namespace Nexus.Agent.Acad;

/// <summary>
/// Print &amp; PDF for drawings: the hub starts AutoCAD's Core Console (accoreconsole.exe, no window) on its own
/// copy of the saved drawing with NEXUS_JOB set, loads this assembly and runs NEXUSJOB, which makes a PDF of
/// each layout and writes the result for the hub. Nothing is saved: the console works on the copy, which the
/// hub deletes afterwards.
/// </summary>
public sealed class HeadlessCommands
{
    [CommandMethod("NEXUSJOB", CommandFlags.Modal)]
    public void Run()
    {
        string? jobPath = Environment.GetEnvironmentVariable(HeadlessJob.EnvironmentVariable);
        if (string.IsNullOrEmpty(jobPath) || !File.Exists(jobPath)) return;
        // The hub's script runs NEXUSJOB, and again after NETLOAD in case the autoloader did not load this
        // assembly: the second run finds the result and does nothing.
        if (File.Exists(Path.ChangeExtension(jobPath, ".done"))) return;
        var log = new ConsoleLog();
        var result = new HeadlessJobResult
        {
            Product = "AutoCAD",
            Version = typeof(HeadlessCommands).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "HostYear")?.Value ?? "",
        };
        HeadlessJob? job = null;
        try
        {
            job = JsonSerializer.Deserialize<HeadlessJob>(File.ReadAllText(jobPath), Nexus.Contracts.Json.Options)
                  ?? throw new InvalidOperationException("Empty job.");
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No drawing is open in the Core Console.");
            log.Info($"PDF job on {doc.Name}");
            result.Pdf = Plot(doc, job.Pdf ?? throw new InvalidOperationException("The job has no PDF request."), log);
        }
        catch (System.Exception ex)
        {
            result.Error = ex.Message;
            log.Error("Job failed.", ex);
        }
        try
        {
            string target = job?.ResultPath is { Length: > 0 } p ? p : Path.ChangeExtension(jobPath, ".result.json");
            File.WriteAllText(target, JsonSerializer.Serialize(result, Nexus.Contracts.Json.Options));
            File.WriteAllText(Path.ChangeExtension(jobPath, ".done"), "");
        }
        catch (System.Exception ex)
        {
            log.Error("Could not write the job result.", ex);
        }
    }

    // ------------------------------------------------------------------ PDF

    private const string PdfDevice = "DWG To PDF.pc3";

    /// <summary>
    /// One PDF per layout with the layout's own page setup (plot area, scale, plot style, rotation), sent to
    /// AutoCAD's "DWG To PDF" with the same paper size (by name, else the closest size).
    /// </summary>
    private static ExportPdfResult Plot(Document doc, ExportPdfRequest request, ConsoleLog log)
    {
        var result = new ExportPdfResult();
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(request.OutputFolder);
        var db = doc.Database;
        object? background = null;
        try { background = AcApp.GetSystemVariable("BACKGROUNDPLOT"); AcApp.SetSystemVariable("BACKGROUNDPLOT", (short)0); }
        catch { /* not settable: plots in the foreground anyway in the console */ }

        string file = Path.GetFileNameWithoutExtension(doc.Name);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layouts = new List<Layout>();
            foreach (DBDictionaryEntry entry in dict)
                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout l && !l.ModelType)
                    layouts.Add(l);
            var wanted = new HashSet<string>(request.ItemIds, StringComparer.OrdinalIgnoreCase);

            foreach (var layout in layouts.OrderBy(l => l.TabOrder))
            {
                string handle = layout.ObjectId.Handle.ToString();
                if (wanted.Count > 0 && !wanted.Contains(handle)) continue;
                string number = request.Numbers.TryGetValue(handle, out var n) ? n : "";
                string name = request.Names.TryGetValue(handle, out var t) && t.Length > 0 ? t : layout.LayoutName;
                var sheet = new ExportedSheet { ItemId = handle, Number = number, Name = name };
                result.Sheets.Add(sheet);
                try
                {
                    string target = PdfNames.Unique(request.OutputFolder, PdfNames.For(request.FileNamePattern, number, name, file), taken);
                    if (File.Exists(target)) File.Delete(target);
                    PlotLayout(layout, target, log);
                    if (File.Exists(target)) sheet.PdfPath = target;
                    else sheet.Error = "AutoCAD did not create the PDF.";
                }
                catch (System.Exception ex)
                {
                    sheet.Error = ex.Message;
                    log.Warn($"PDF of layout '{layout.LayoutName}' failed.", ex);
                }
            }
            foreach (var id in wanted.Where(id => result.Sheets.All(s => !s.ItemId.Equals(id, StringComparison.OrdinalIgnoreCase))))
                result.Sheets.Add(new ExportedSheet { ItemId = id, Error = "Layout not found in this drawing (it may have been deleted)." });
            tr.Commit();
        }

        if (background is not null)
        {
            try { AcApp.SetSystemVariable("BACKGROUNDPLOT", background); } catch { /* ignored */ }
        }
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static void PlotLayout(Layout layout, string target, ConsoleLog log)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException("Another plot is running.");

        LayoutManager.Current.CurrentLayout = layout.LayoutName;
        var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        var validator = PlotSettingsValidator.Current;
        var paper = layout.PlotPaperSize;
        string media = layout.CanonicalMediaName;
        validator.SetPlotConfigurationName(settings, PdfDevice, null);
        validator.RefreshLists(settings);
        var names = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
        string? chosen = names.FirstOrDefault(n => string.Equals(n, media, StringComparison.OrdinalIgnoreCase))
                         ?? ClosestMedia(validator, settings, names, paper.X, paper.Y);
        if (chosen is not null) validator.SetCanonicalMediaName(settings, chosen);
        log.Info($"Layout '{layout.LayoutName}': {media} → {chosen ?? "(device default)"}");

        var info = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings };
        new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);

        using var engine = PlotFactory.CreatePublishEngine();
        using var page = new PlotPageInfo();
        engine.BeginPlot(null, null);
        engine.BeginDocument(info, layout.LayoutName, null, 1, true, target);
        engine.BeginPage(page, info, true, null);
        engine.BeginGenerateGraphics(null);
        engine.EndGenerateGraphics(null);
        engine.EndPage(null);
        engine.EndDocument(null);
        engine.EndPlot(null);
    }

    /// <summary>The PDF paper whose size (either way round) is nearest the layout's paper.</summary>
    private static string? ClosestMedia(PlotSettingsValidator validator, PlotSettings settings, List<string> names, double w, double h)
    {
        double a = Math.Min(w, h), b = Math.Max(w, h);
        string? best = null;
        double bestScore = double.MaxValue;
        foreach (var name in names)
        {
            try
            {
                validator.SetCanonicalMediaName(settings, name);
                var size = settings.PlotPaperSize;
                double score = Math.Abs(Math.Min(size.X, size.Y) - a) + Math.Abs(Math.Max(size.X, size.Y) - b);
                // Prefer "full bleed" papers (no hardware margins), like the original paper.
                if (name.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0) score -= 0.5;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = name;
                }
            }
            catch { /* skip */ }
        }
        return best;
    }
}

/// <summary>A small log for the Core Console part (%LOCALAPPDATA%\Nexus\logs\acad-pdf-*.log).</summary>
internal sealed class ConsoleLog
{
    private readonly string _path;

    public ConsoleLog()
    {
        _path = Path.Combine(NexusPaths.LogsDir, $"acad-pdf-{DateTime.Now:yyyyMMdd}.log");
        try { Directory.CreateDirectory(NexusPaths.LogsDir); } catch { /* ignored */ }
    }

    public void Info(string message) => Write("INFO ", message, null);
    public void Warn(string message, System.Exception? ex = null) => Write("WARN ", message, ex);
    public void Error(string message, System.Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, System.Exception? ex)
    {
        try
        {
            File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Process.GetCurrentProcess().Id}] {message}"
                                      + (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine);
        }
        catch { /* never fail the job for the log */ }
    }
}
