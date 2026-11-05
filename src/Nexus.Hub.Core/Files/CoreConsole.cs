using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core.Files;

/// <summary>An installed AutoCAD Core Console with the Nexus add-in for the same release.</summary>
public sealed record CoreConsoleInstall(int Year, string Exe, string AgentDll, bool HasCivil3D)
{
    public string Name => $"AutoCAD {Year}";
}

/// <summary>
/// AutoCAD's Core Console (accoreconsole.exe): AutoCAD without a window, installed with AutoCAD, Civil 3D and
/// Plant 3D. Print &amp; PDF runs it on a copy of a saved drawing to make PDFs of its layouts.
/// </summary>
public static class CoreConsole
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Releases with both the Core Console and the Nexus add-in installed, newest first.</summary>
    public static List<CoreConsoleInstall> Find()
    {
        var list = new List<CoreConsoleInstall>();
        string bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "ApplicationPlugins", "Nexus.bundle", "Contents");
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        for (int year = DateTime.Now.Year + 2; year >= 2024; year--)
        {
            string dir = Path.Combine(programs, "Autodesk", $"AutoCAD {year}");
            string exe = Path.Combine(dir, "accoreconsole.exe");
            string dll = Path.Combine(bundle, year.ToString(System.Globalization.CultureInfo.InvariantCulture), "Nexus.Agent.Acad.Console.dll");
            if (File.Exists(exe) && File.Exists(dll))
                list.Add(new CoreConsoleInstall(year, exe, dll, Directory.Exists(Path.Combine(dir, "C3D"))));
        }
        return list;
    }

    public const string NotInstalled =
        "PDFs of drawings need AutoCAD, Civil 3D or Plant 3D 2024 or later on this PC, with the Nexus add-in built for it " +
        "(Rebuild Solution installs it).";

    /// <summary>Runs one job on <paramref name="drawing"/> (a working copy) and returns what the add-in wrote.</summary>
    public static async Task<HeadlessJobResult> RunAsync(CoreConsoleInstall install, string drawing, HeadlessJob job, string workFolder,
        bool loadCivil3D, CancellationToken ct)
    {
        string jobPath = Path.Combine(workFolder, "nexus-job.json");
        job.ResultPath = Path.Combine(workFolder, "nexus-result.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, Json.Options), ct).ConfigureAwait(false);
        string script = Path.Combine(workFolder, "nexus-job.scr");
        // The autoloader normally has the add-in loaded already (it is in the Nexus bundle): run the job. If not,
        // NETLOAD it (the file is asked for on the command line; FILEDIA is off in the console) and run again;
        // a job that already ran does nothing the second time.
        await File.WriteAllTextAsync(script, $"NEXUSJOB\n_.NETLOAD\n\"{install.AgentDll}\"\nNEXUSJOB\n", ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo(install.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.Unicode, // the console writes UTF-16
            WorkingDirectory = workFolder,
        };
        psi.ArgumentList.Add("/i");
        psi.ArgumentList.Add(drawing);
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add(script);
        if (loadCivil3D && install.HasCivil3D)
        {
            // Civil 3D objects (surfaces, alignments...) draw as themselves, not as proxies.
            psi.ArgumentList.Add("/product");
            psi.ArgumentList.Add("C3D");
        }
        psi.Environment[HeadlessJob.EnvironmentVariable] = jobPath;

        var output = new StringBuilder();
        bool blocked = false;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output)
            {
                if (output.Length < 64_000) output.AppendLine(e.Data.Replace("\0", ""));
            }
            // Security prompt for an add-in outside AutoCAD's trusted locations: it would wait for an answer.
            if (e.Data.Contains("Always load", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("SECURELOAD", StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
                try { timeout.Cancel(); } catch { /* disposed */ }
            }
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        var sw = Stopwatch.StartNew();
        HubLog.Info($"Core Console {install.Year}: PDFs of {Path.GetFileName(drawing)}");
        if (!process.Start()) throw Failure("AutoCAD's Core Console did not start.");
        process.StandardInput.Close(); // nothing to answer: a question ends the console instead of waiting
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (blocked)
                throw Failure($"AutoCAD {install.Year} asked whether to load the Nexus add-in (security setting SECURELOAD). " +
                              $"Add {Path.GetDirectoryName(install.AgentDll)} to Options › Files › Trusted Locations in AutoCAD, then try again.");
            if (ct.IsCancellationRequested) throw;
            throw Failure($"AutoCAD's Core Console did not finish within {Timeout.TotalMinutes:0} minutes.");
        }
        HubLog.Info($"Core Console {install.Year}: {Path.GetFileName(drawing)} done in {sw.ElapsedMilliseconds} ms (exit {process.ExitCode})");

        if (!File.Exists(job.ResultPath))
        {
            string tail;
            lock (output) tail = Tail(output.ToString());
            HubLog.Warn($"Core Console output for {Path.GetFileName(drawing)}:\n{tail}");
            string hint = tail.IndexOf("Unable to load", StringComparison.OrdinalIgnoreCase) >= 0
                ? $" AutoCAD would not load {Path.GetFileName(install.AgentDll)}: rebuild the solution with AutoCAD closed, then if it still fails add " +
                  $"{Path.GetDirectoryName(install.AgentDll)} to AutoCAD's Options › Files › Trusted Locations."
                : "";
            throw Failure($"AutoCAD {install.Year} could not make the PDFs.{hint} The last lines it wrote: {tail.Replace(Environment.NewLine, " ").Trim()}");
        }
        var result = JsonSerializer.Deserialize<HeadlessJobResult>(await File.ReadAllTextAsync(job.ResultPath, ct).ConfigureAwait(false), Json.Options)
                     ?? throw Failure("The add-in wrote an empty result.");
        if (result.Error is not null) throw Failure($"AutoCAD {install.Year}: {result.Error}");
        return result;
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 6)));
    }

    private static AgentRequestException Failure(string message) => new(new ErrorInfo(ErrorCodes.InternalError, message));
}
