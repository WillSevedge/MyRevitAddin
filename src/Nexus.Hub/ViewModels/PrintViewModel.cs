using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;
using Nexus.Hub.Pdf;

namespace Nexus.Hub.ViewModels;

/// <summary>One sheet in Print &amp; PDF.</summary>
public sealed class PrintSheet : Observable
{
    private bool _include = true;
    private string _status = "";
    private string? _pdf;
    private bool _failed;

    public required TableRow Row { get; init; }
    public required ResultRun Run { get; init; }
    public required string Number { get; init; }
    public required string Name { get; init; }
    public string File => Row.Document;
    public string ItemId => Row.ItemId;
    /// <summary>"Revit 2026", "Civil 3D 2026"...</summary>
    public string Source => Run.Host.Name;
    public string Title => Number.Length > 0 ? $"{Number} - {Name}" : Name;

    public bool Include { get => _include; set => Set(ref _include, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Failed { get => _failed; set => Set(ref _failed, value); }

    /// <summary>The sheet's PDF once made (in the output folder, or a preview copy).</summary>
    public string? PdfPath
    {
        get => _pdf;
        set
        {
            if (Set(ref _pdf, value)) Raise(nameof(HasPdf));
        }
    }

    public bool HasPdf => _pdf is not null && System.IO.File.Exists(_pdf);
}

/// <summary>
/// Print &amp; PDF: PDFs of sheets made by the programs themselves (Revit's PDF export; AutoCAD's DWG To PDF
/// with each layout's page setup), from the open files; a preview of each sheet; one combined PDF;
/// and printing to any printer, all without opening the PDFs in another program.
/// </summary>
public sealed class PrintViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly DrawingPdfs _drawings;
    private PrintSheet? _selected;
    private BitmapSource? _preview;
    private string _previewText = "";
    private string _status = "";
    private bool _busy;
    private CancellationTokenSource? _cts;
    private string _folder;
    private string _pattern;
    private bool _combine;
    private string _combinedName;
    private bool _matchSheetSize;

    public PrintViewModel(MainViewModel main, DrawingPdfs drawings, IEnumerable<PrintSheet> sheets, string scope, string? folder, string? pattern)
    {
        _main = main;
        _drawings = drawings;
        Scope = scope;
        foreach (var s in sheets) Sheets.Add(s);
        _folder = folder is { Length: > 0 } && Directory.Exists(folder)
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nexus PDFs");
        _pattern = pattern is { Length: > 0 } ? pattern : PdfNames.DefaultPattern;
        _combinedName = $"Sheets {DateTime.Now:yyyy-MM-dd}";
        CreateCommand = new RelayCommand(() => CreatePdfsAsync(onlyMissing: false), () => !_busy && Included.Count > 0);
        PrintCommand = new RelayCommand(PrintAsync, () => !_busy && Included.Count > 0);
        PreviewCommand = new RelayCommand(PreviewSelectedAsync, () => !_busy && _selected is not null);
        CancelCommand = new RelayCommand(() => { _cts?.Cancel(); return Task.CompletedTask; }, () => _busy);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
            return Task.CompletedTask;
        });
        SelectAllCommand = new RelayCommand(() => { foreach (var s in Sheets) s.Include = true; RaiseCounts(); return Task.CompletedTask; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var s in Sheets) s.Include = false; RaiseCounts(); return Task.CompletedTask; });
        foreach (var s in Sheets) s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PrintSheet.Include)) RaiseCounts(); };
        Selected = Sheets.FirstOrDefault();
        Status = "PDFs are made by Revit and AutoCAD themselves, with each sheet's own size and page setup.";
    }

    public string Scope { get; }
    public ObservableCollection<PrintSheet> Sheets { get; } = new();
    public RelayCommand CreateCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public List<PrintSheet> Included => Sheets.Where(s => s.Include).ToList();
    public string CountText => $"{Included.Count} of {Sheets.Count} sheets";

    public string Folder { get => _folder; set => Set(ref _folder, value?.Trim() ?? ""); }
    public string Pattern { get => _pattern; set => Set(ref _pattern, value ?? ""); }
    public bool Combine { get => _combine; set => Set(ref _combine, value); }
    public string CombinedName { get => _combinedName; set => Set(ref _combinedName, value ?? ""); }
    public bool MatchSheetSize { get => _matchSheetSize; set => Set(ref _matchSheetSize, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public PrintSheet? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) _ = ShowPreviewAsync();
        }
    }

    public BitmapSource? Preview { get => _preview; private set { if (Set(ref _preview, value)) Raise(nameof(HasPreview)); } }
    public bool HasPreview => _preview is not null;
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }

    private void RaiseCounts()
    {
        Raise(nameof(CountText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ preview

    private async Task ShowPreviewAsync()
    {
        var sheet = _selected;
        Preview = null;
        if (sheet is null)
        {
            PreviewText = "";
            return;
        }
        if (!sheet.HasPdf)
        {
            PreviewText = "No PDF of this sheet yet. Preview makes one (only this sheet); Create PDFs makes them all.";
            return;
        }
        PreviewText = "Loading the preview…";
        try
        {
            var (pages, w, h) = await PdfPages.InfoAsync(sheet.PdfPath!);
            var image = await PdfPages.RenderAsync(sheet.PdfPath!, 0, 2200);
            if (!ReferenceEquals(sheet, _selected)) return;
            Preview = image;
            PreviewText = $"{sheet.Title}  ·  {w:0.##} × {h:0.##} in" + (pages > 1 ? $"  ·  {pages} pages" : "");
        }
        catch (Exception ex)
        {
            PreviewText = "The preview could not be shown: " + ex.Message;
        }
    }

    /// <summary>A PDF of the selected sheet only, in a preview folder (not the output folder).</summary>
    private async Task PreviewSelectedAsync()
    {
        if (_selected is not { } sheet) return;
        string folder = Path.Combine(NexusPaths.Root, "preview", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await RunAsync($"Making a preview of {sheet.Title}", ct => ExportAsync(new[] { sheet }, folder, PdfNames.DefaultPattern, ct));
        await ShowPreviewAsync();
    }

    // ------------------------------------------------------------------ PDFs

    private async Task CreatePdfsAsync(bool onlyMissing)
    {
        var sheets = Included.Where(s => !onlyMissing || !s.HasPdf).ToList();
        if (sheets.Count == 0) return;
        if (string.IsNullOrWhiteSpace(_folder))
        {
            Status = "Pick a folder for the PDFs first.";
            return;
        }
        bool ok = await RunAsync($"Making {sheets.Count} PDF{(sheets.Count == 1 ? "" : "s")}", ct => ExportAsync(sheets, _folder, _pattern, ct));
        if (!ok) return;
        _main.RememberPdfSettings(_folder, _pattern);

        int made = sheets.Count(s => s.HasPdf);
        string summary = $"{made} of {sheets.Count} PDFs made in {_folder}.";
        if (_combine && !onlyMissing)
        {
            // Page labels = sheet numbers and bookmarks = "A101 - Floor Plan": Bluebeam Revu (and other viewers)
            // navigate, hyperlink and slip-sheet by them.
            var parts = Included.Where(s => s.HasPdf).Select(s => new PdfPart(s.PdfPath!, s.Number, s.Title)).ToList();
            if (parts.Count > 0)
            {
                string name = PdfNames.For(_combinedName, "", "", "");
                string target = Path.Combine(_folder, name + ".pdf");
                try
                {
                    await Task.Run(() => PdfTools.Combine(parts, target, name));
                    summary += $" Combined: {name}.pdf ({parts.Count} sheets, bookmarked and labelled by sheet number).";
                }
                catch (Exception ex)
                {
                    summary += " Could not combine them: " + ex.Message;
                }
            }
        }
        Status = summary + (sheets.Any(s => s.Failed) ? " Some sheets failed: see Status." : "");
        await ShowPreviewAsync();
    }

    /// <summary>Sends each file's sheets to the program that can make them (one request per file).</summary>
    private async Task ExportAsync(IReadOnlyList<PrintSheet> sheets, string folder, string pattern, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        // The same sheet number in two files would give the same file name: add the file name then.
        bool clash = sheets.GroupBy(s => PdfNames.For(pattern, s.Number, s.Name, ""), StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        if (clash && !pattern.Contains("{File}")) pattern = "{File} - " + pattern;

        foreach (var s in sheets)
        {
            s.Status = "Waiting…";
            s.Failed = false;
        }
        var groups = sheets.GroupBy(s => s.Run).ToList();
        await Task.WhenAll(groups.Select(async g =>
        {
            var list = g.ToList();
            foreach (var s in list) s.Status = "Making the PDF…";
            try
            {
                var request = new ExportPdfRequest
                {
                    ItemIds = list.Select(s => s.ItemId).ToList(),
                    OutputFolder = folder,
                    FileNamePattern = pattern,
                    Numbers = list.GroupBy(s => s.ItemId).ToDictionary(x => x.Key, x => x.First().Number),
                    Names = list.GroupBy(s => s.ItemId).ToDictionary(x => x.Key, x => x.First().Name),
                };
                var (result, note) = await ExportFileAsync(g.Key, request, ct);
                foreach (var s in list)
                {
                    var done = result.Sheets.FirstOrDefault(x => string.Equals(x.ItemId, s.ItemId, StringComparison.OrdinalIgnoreCase));
                    if (done?.PdfPath is { } pdf)
                    {
                        s.PdfPath = pdf;
                        s.Status = "PDF made" + note;
                    }
                    else
                    {
                        s.Failed = true;
                        s.Status = done?.Error ?? "No PDF was made for this sheet.";
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                foreach (var s in list.Where(s => !s.HasPdf)) s.Status = "Cancelled";
            }
            catch (Exception ex)
            {
                string message = ex is AgentRequestException a ? a.Error.Message : ex.Message;
                foreach (var s in list)
                {
                    s.Failed = true;
                    s.Status = message;
                }
                HubLog.Warn($"PDFs of {g.Key.DocumentTitle} failed: {message}");
            }
        }));
    }

    /// <summary>
    /// Revit: Revit makes the PDFs from the open model (unsaved changes included), one model at a time per Revit.
    /// Drawings: AutoCAD's Core Console makes them from a copy of the saved drawing (AutoCAD keeps working).
    /// </summary>
    private async Task<(ExportPdfResult Result, string Note)> ExportFileAsync(ResultRun run, ExportPdfRequest request, CancellationToken ct)
    {
        var agent = run.Agent?.Connection ?? throw new InvalidOperationException($"{run.DocumentTitle} is no longer open.");
        if (agent.Host.HostKind == HostKinds.Revit)
        {
            if (!agent.CanExportPdf)
                throw new InvalidOperationException($"{agent.Host.Name}'s Nexus add-in is out of date. Close Revit, Rebuild Solution, and open it again.");
            request.DocumentId = run.Result?.DocumentId;
            return (await OneAtATime(agent.Host.ProcessId, () => agent.ExportPdfAsync(request, ct), ct), "");
        }

        var doc = agent.Documents.FirstOrDefault(d => d.Id == run.Result?.DocumentId);
        if (doc?.Path is not { Length: > 0 } path || !System.IO.File.Exists(path))
            throw new InvalidOperationException($"{run.DocumentTitle} has never been saved: save it first (PDFs are made from the saved drawing).");
        return (await _drawings.ExportAsync(path, request, ct), doc.IsModified ? " (from the last save)" : "");
    }

    // Revit works on one thing at a time: a second export waiting behind a long one would time out as "busy".
    private static readonly Dictionary<int, SemaphoreSlim> Queues = new();

    private static async Task<T> OneAtATime<T>(int processId, Func<Task<T>> work, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (Queues)
        {
            if (!Queues.TryGetValue(processId, out gate!)) Queues[processId] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct);
        try { return await work(); }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ print

    private async Task PrintAsync()
    {
        if (Included.Any(s => !s.HasPdf))
        {
            await CreatePdfsAsync(onlyMissing: true);
            if (Included.Any(s => !s.HasPdf))
            {
                Status = "Some sheets have no PDF, so nothing was printed. See Status for why.";
                return;
            }
        }
        var dialog = new System.Windows.Controls.PrintDialog { UserPageRangeEnabled = false };
        if (dialog.ShowDialog() != true) return;
        var pdfs = Included.Select(s => s.PdfPath!).ToList();
        int pages = pdfs.Count;
        await RunAsync($"Printing {pages} sheet{(pages == 1 ? "" : "s")}", async ct =>
        {
            var progress = new Progress<string>(text => Status = text);
            await PdfPages.PrintAsync(pdfs, dialog.PrintQueue, dialog.PrintTicket, _matchSheetSize, dpi: 300, progress, ct);
        });
        Status = $"Sent {pages} sheet{(pages == 1 ? "" : "s")} to {dialog.PrintQueue.FullName}.";
    }

    // ------------------------------------------------------------------ helpers

    private async Task<bool> RunAsync(string what, Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = what + "…";
        try
        {
            await work(_cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            HubLog.Warn($"{what} failed.", ex);
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}
