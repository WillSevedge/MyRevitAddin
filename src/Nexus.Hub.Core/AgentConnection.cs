using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>A discovered agent plus its live connection and last-known state.</summary>
public sealed class AgentConnection : IAsyncDisposable
{
    public AgentConnection(AgentRegistration registration) => Registration = registration;

    public AgentRegistration Registration { get; private set; }
    public HostInfo Host => Registration.Host;
    public AgentClient? Client { get; private set; }
    public List<DocumentInfo> Documents { get; private set; } = new();
    public List<ReaderDescriptor> Readers { get; private set; } = new();
    public string? LastError { get; private set; }

    /// <summary>
    /// The program was busy (opening or downloading a model, a dialog, a command) and did not answer in time.
    /// Not a failure: the hub tries again by itself.
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>(Re)connects if needed and refreshes host info, documents and readers.</summary>
    public async Task RefreshAsync(AgentRegistration latest, CancellationToken ct = default)
    {
        Registration = latest;
        LastError = null;
        IsBusy = false;
        try
        {
            var client = await ConnectedAsync(ct).ConfigureAwait(false);
            var hello = await client.HelloAsync(ct).ConfigureAwait(false);
            Registration.Host = hello.Host;
            Readers = await client.ListReadersAsync(ct).ConfigureAwait(false);
            Documents = await client.ListDocumentsAsync(ct).ConfigureAwait(false);
        }
        catch (AgentRequestException ex) when (ex.Code == ErrorCodes.HostBusy)
        {
            // The files known so far are kept.
            IsBusy = true;
            LastError = $"{Host.Product} is busy (opening a model, a dialog or a command). Nexus will check again in a few seconds.";
            HubLog.Info($"{Host.DisplayName}: busy ({ex.Error.Message}); will retry.");
        }
        catch (AgentRequestException ex)
        {
            LastError = ex.Error.ToString();
            HubLog.Warn($"{Host.DisplayName}: {LastError}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            HubLog.Warn($"{Host.DisplayName}: could not connect.", ex);
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>True when the agent accepts edits.</summary>
    public bool CanWrite => Host.Features.Contains(AgentFeatures.Write);

    public async Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct = default) =>
        await (await ConnectedAsync(ct).ConfigureAwait(false)).ReadAsync(request, ct).ConfigureAwait(false);

    public async Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct = default) =>
        await (await ConnectedAsync(ct).ConfigureAwait(false)).WriteAsync(request, ct).ConfigureAwait(false);

    public async Task<SelectResult> SelectAsync(SelectRequest request, CancellationToken ct = default) =>
        await (await ConnectedAsync(ct).ConfigureAwait(false)).SelectAsync(request, ct).ConfigureAwait(false);

    public async Task<ExportPdfResult> ExportPdfAsync(ExportPdfRequest request, CancellationToken ct = default) =>
        await (await ConnectedAsync(ct).ConfigureAwait(false)).ExportPdfAsync(request, ct).ConfigureAwait(false);

    public async Task<CreateSheetsResult> CreateSheetsAsync(CreateSheetsRequest request, CancellationToken ct = default) =>
        await (await ConnectedAsync(ct).ConfigureAwait(false)).CreateSheetsAsync(request, ct).ConfigureAwait(false);

    /// <summary>True when the agent can create sheets.</summary>
    public bool CanCreateSheets => Host.Features.Contains(AgentFeatures.CreateSheets);

    /// <summary>Create-sheets requests may also update sheets (dummy sheet sync for the drawing index).</summary>
    public bool CanSyncSheets => Host.Features.Contains(AgentFeatures.SyncSheets);

    /// <summary>True when the agent can make PDFs of sheets.</summary>
    public bool CanExportPdf => Host.Features.Contains(AgentFeatures.ExportPdf);

    /// <summary>True when the agent can show items (open sheets, select objects).</summary>
    public bool CanSelect => Host.Features.Contains(AgentFeatures.Select);

    // One reconnect at a time: a refresh and a read must not both replace (and dispose) the client.
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private async Task<AgentClient> ConnectedAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Client is null || !Client.IsConnected)
            {
                var old = Client;
                Client = null;
                if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
                Client = await AgentClient.ConnectAsync(Registration.PipeName, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            return Client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var old = Client;
        Client = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }
}
