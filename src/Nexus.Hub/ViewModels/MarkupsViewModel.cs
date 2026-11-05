using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Excel;
using Nexus.Hub.Core.Files;

namespace Nexus.Hub.ViewModels;

/// <summary>One markup in the list, with the sheet it is on as the model has it.</summary>
public sealed class MarkupRow
{
    public MarkupRow(PdfMarkup m, string path, TableRow? model)
    {
        M = m;
        Path = path;
        Model = model;
    }

    public PdfMarkup M { get; }
    public string Path { get; }
    public TableRow? Model { get; }

    public string Sheet => M.Sheet.Length > 0 ? M.Sheet : $"Page {M.Page}";
    public string SheetName => Model is not null && Model.Values.TryGetValue(SheetFieldMap.ColumnId("Title"), out var t) ? t.Value ?? "" : "";
    /// <summary>"A101 · Floor Plan" for the group headers.</summary>
    public string Group => SheetName.Length > 0 ? $"{Sheet} · {SheetName}" : M.Sheet.Length > 0 ? $"{Sheet} · not in the loaded models" : Sheet;
    public bool InModel => Model is not null;
    public int Page => M.Page;
    public string Type => M.Type;
    public string Subject => M.Subject;
    public string Comment => M.Comment;
    public string Author => M.Author;
    public string Date => M.Date?.ToString("g") ?? "";
    public DateTime? When => M.Date;
    public string Status => M.Status.Length > 0 ? M.Status : "Open";
    public bool IsOpen => M.IsOpen;
    public string Replies => M.Replies == 0 ? "" : M.Replies.ToString();
    public string RepliesText => string.Join(Environment.NewLine, M.ReplyTexts);
    public string File => M.File;
}

/// <summary>
/// Markups from Bluebeam (or any PDF reviewer) next to the sheets: which drawings have open comments, who made
/// them and what was answered. The PDFs are only read; they are read again whenever Bluebeam saves them.
/// </summary>
public sealed class MarkupsViewModel : Observable, IDisposable
{
    public static readonly string[] Filters = { "Open", "Everything", "Closed", "On sheets not in the model" };

    private readonly MainViewModel _main;
    private readonly List<FileSystemWatcher> _watchers = new();
    private CancellationTokenSource? _saveDelay;
    private string _filter = Filters[0];
    private string _search = "";
    private string _summary = "";
    private string _message = "";
    private bool _busy;

    public MarkupsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var f in main.MarkupPdfs.Where(File.Exists)) Files.Add(f);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is MarkupRow r && Shows(r);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MarkupRow.Group)));
        AddCommand = new RelayCommand(AddAsync);
        RemoveCommand = new RelayCommand(() => { Remove(); return Task.CompletedTask; }, () => SelectedFile is not null);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_busy && Files.Count > 0);
        ExportCommand = new RelayCommand(() => { Export(); return Task.CompletedTask; }, () => Rows.Count > 0);
        OpenPdfCommand = new RelayCommand(() => { OpenPdf(); return Task.CompletedTask; }, () => Files.Count > 0);
        ShowInModelCommand = new RelayCommand(ShowInModelAsync);
    }

    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<MarkupRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenPdfCommand { get; }
    public RelayCommand ShowInModelCommand { get; }

    /// <summary>The window supplies the selected markups.</summary>
    public Func<IReadOnlyList<MarkupRow>>? SelectedRows { get; set; }

    private string? _selectedFile;
    public string? SelectedFile
    {
        get => _selectedFile;
        set => Set(ref _selectedFile, value);
    }

    public bool HasFiles => Files.Count > 0;

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) View.Refresh();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? "")) View.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    private bool Shows(MarkupRow r)
    {
        bool kind = _filter switch
        {
            "Everything" => true,
            "Closed" => !r.IsOpen,
            "On sheets not in the model" => !r.InModel,
            _ => r.IsOpen,
        };
        if (!kind) return false;
        string s = _search.Trim();
        return s.Length == 0 || new[] { r.Sheet, r.SheetName, r.Comment, r.Subject, r.Author, r.Status, r.RepliesText }
            .Any(v => v.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ files

    private async Task AddAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the reviewed PDFs (Bluebeam sets or single sheets)",
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var f in dialog.FileNames)
            if (!Files.Contains(f, StringComparer.OrdinalIgnoreCase)) Files.Add(f);
        Remember();
        await RefreshAsync();
    }

    private void Remove()
    {
        if (SelectedFile is null) return;
        Files.Remove(SelectedFile);
        Remember();
        _ = RefreshAsync();
    }

    private void Remember()
    {
        _main.MarkupPdfs = Files.ToList();
        Raise(nameof(HasFiles));
        Watch();
    }

    /// <summary>Reads every PDF (in the background) and matches each page to a loaded sheet.</summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var paths = Files.ToList();
            var results = await Task.Run(() => paths.Select(p =>
            {
                try { return (Path: p, Markups: PdfMarkups.Read(p), Error: (string?)null); }
                catch (Exception ex) { return (Path: p, Markups: new List<PdfMarkup>(), Error: ex.Message); }
            }).ToList());

            var sheets = _main.SheetsByNumber();
            Rows.Clear();
            foreach (var (path, markups, _) in results)
                foreach (var m in markups.OrderBy(m => m.Sheet, NaturalComparer.Instance).ThenBy(m => m.Page).ThenBy(m => m.Date))
                    Rows.Add(new MarkupRow(m, path, m.Sheet.Length > 0 && sheets.TryGetValue(ExcelCompare.NormalizeKey(m.Sheet), out var row) ? row : null));

            int open = Rows.Count(r => r.IsOpen);
            int onSheets = Rows.Select(r => r.Sheet).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int notInModel = Rows.Count(r => !r.InModel);
            Summary = Rows.Count == 0
                ? (Files.Count == 0 ? "Add the reviewed PDFs to see their markups." : "No markups in these PDFs.")
                : $"{Rows.Count} markup(s) on {onSheets} sheet(s) · {open} open · {Rows.Count - open} closed" +
                  (notInModel > 0 ? $" · {notInModel} on sheets not in the loaded models" : "");
            var errors = results.Where(r => r.Error is not null).Select(r => $"{Path.GetFileName(r.Path)}: {r.Error}").ToList();
            Message = errors.Count > 0
                ? "Could not read " + string.Join("; ", errors)
                : sheets.Count == 0 && Rows.Count > 0
                    ? "Show Sheets in the main window to see each sheet's name and to open sheets in Revit or AutoCAD."
                    : Rows.Count > 0 ? $"Read {DateTime.Now:t}. Saves in Bluebeam show up here by themselves." : "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Bluebeam saves the markups into the PDF: read again shortly after a save.</summary>
    private void Watch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in Files.Select(f => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(f))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder, "*.pdf") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void OnEvent(string path)
                {
                    if (Files.Contains(path, StringComparer.OrdinalIgnoreCase))
                        Application.Current?.Dispatcher.BeginInvoke(OnSaved);
                }
                w.Changed += (_, e) => OnEvent(e.FullPath);
                w.Created += (_, e) => OnEvent(e.FullPath);
                w.Renamed += (_, e) => OnEvent(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                HubLog.Warn("Could not watch the markup PDFs.", ex);
            }
        }
    }

    private async void OnSaved()
    {
        _saveDelay?.Cancel();
        var delay = _saveDelay = new CancellationTokenSource();
        try { await Task.Delay(1500, delay.Token); }
        catch (OperationCanceledException) { return; }
        await RefreshAsync();
    }

    public void Start()
    {
        Watch();
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _saveDelay?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    // ------------------------------------------------------------------ actions

    private void OpenPdf()
    {
        string? path = SelectedRows?.Invoke().FirstOrDefault()?.Path ?? SelectedFile ?? Files.FirstOrDefault();
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open the PDF: " + ex.Message; }
    }

    private Task ShowInModelAsync()
    {
        var rows = (SelectedRows?.Invoke() ?? Array.Empty<MarkupRow>()).Where(r => r.Model is not null).Select(r => r.Model!).Distinct().ToList();
        if (rows.Count == 0)
        {
            Message = "Select markups on sheets that are in the loaded models (Sheets view) to open those sheets.";
            return Task.CompletedTask;
        }
        return _main.ShowInModelAsync(rows);
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Markups {DateTime.Now:yyyy-MM-dd}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var headers = new[] { "Sheet", "Sheet Name", "Page", "Type", "Subject", "Comment", "Author", "Date", "Status", "Replies", "File" };
            var rows = View.Cast<MarkupRow>().Select(r => (IReadOnlyList<string?>)new[]
            {
                r.Sheet, r.SheetName, r.Page.ToString(), r.Type, r.Subject, r.Comment, r.Author, r.Date, r.Status, r.RepliesText, r.File,
            });
            ExcelWorkbook.Export(dialog.FileName, headers, rows, "Markups");
            Message = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            Message = "Could not save: " + ex.Message;
        }
    }
}
