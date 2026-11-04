using System.Windows;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub.ViewModels;

/// <summary>Sheet index: dummy sheets in the Revit model that holds the drawing index, kept in step with the DWGs.</summary>
public sealed partial class MainViewModel
{
    private SheetIndexViewModel? _sheetIndex;

    internal UiSettings UiSettings => _settings;

    /// <summary>Open Revit models whose add-in can sync dummy sheets.</summary>
    internal List<DocumentNode> IndexTargets() =>
        Agents.SelectMany(a => a.Documents).Where(d => d.Agent.Connection.CanSyncSheets).ToList();

    public RelayCommand SheetIndexCommand => _sheetIndexCommand ??= new RelayCommand(OpenSheetIndexAsync);
    private RelayCommand? _sheetIndexCommand;

    private Task OpenSheetIndexAsync()
    {
        if (_sheetIndex is not null)
        {
            _sheetIndex.Window?.Activate();
            return _sheetIndex.RefreshAsync();
        }
        var vm = new SheetIndexViewModel(this);
        var window = new SheetIndexWindow(vm) { Owner = Application.Current.MainWindow };
        vm.Window = window;
        _sheetIndex = vm;
        window.Closed += (_, _) => _sheetIndex = null;
        window.Show();
        return vm.RefreshAsync();
    }

    /// <summary>
    /// Sends the dummy sheet changes to the index model (one transaction: one Undo in Revit), then reads the sheets
    /// again. Null when nothing was sent (the user kept unapplied edits).
    /// </summary>
    internal async Task<CreateSheetsResult?> SyncIndexAsync(DocumentNode target, CreateSheetsRequest request)
    {
        if (!ConfirmDiscardEdits()) return null;
        Status = $"Syncing {request.Sheets.Count} dummy sheet(s) in {target.Title}…";
        CreateSheetsResult result;
        try
        {
            result = await target.Agent.Connection.CreateSheetsAsync(request);
        }
        catch (AgentRequestException ex)
        {
            HubLog.Warn($"Dummy sheet sync in {target.Title} failed: {ex.Error}");
            throw;
        }
        int created = result.Sheets.Count(s => s.Created), updated = result.Sheets.Count(s => s.Updated);
        HubLog.Info($"{result.DocumentTitle}: dummy sheet sync: {created} created, {updated} updated" +
                    (result.UndoName is null ? "" : $" (Undo: \"{result.UndoName}\")"));
        if (created + updated > 0) await LoadAsync(confirm: false);
        return result;
    }
}
