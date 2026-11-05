using Nexus.Agent;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class AgentRoundTripTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "nexus-tests-" + Guid.NewGuid().ToString("N"));
    private AgentLog _log = null!;
    private FakeHost _host = null!;
    private FakeDocs _docs = null!;
    private FakeSheetsReader _sheets = null!;
    private AgentServer<FakeDoc> _server = null!;
    private HostInfo _info = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("NEXUS_HOME", _home);
        _log = new AgentLog("test");
        _host = new FakeHost(_log);
        _docs = new FakeDocs();
        _sheets = new FakeSheetsReader();

        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(_sheets);
        readers.Register(new ThrowingReader());
        readers.Register(new PlaceholderReader<FakeDoc>("fake.rooms", "Rooms", "Spaces", "TODO"));

        _info = HostInfoFactory.Create("Fake", "FakeCAD", "2026", "test", _log);
        // Unique host kind so parallel test classes never share a pipe name.
        _info.HostKind = "Fake" + Guid.NewGuid().ToString("N")[..8];
        _server = new AgentServer<FakeDoc>(_info, _host.Queue, _docs, readers, _log,
            new AgentServerOptions { HostStartTimeout = TimeSpan.FromMilliseconds(500) });
        _server.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        _host.Dispose();
        try { Directory.Delete(_home, true); } catch { /* ignored */ }
        return Task.CompletedTask;
    }

    private Task<AgentClient> Connect() => AgentClient.ConnectAsync(_server.PipeName, TimeSpan.FromSeconds(5));

    [Fact]
    public void Registration_is_discoverable()
    {
        var found = AgentDiscovery.Discover();
        var mine = Assert.Single(found, a => a.PipeName == _server.PipeName);
        Assert.Equal("FakeCAD", mine.Host.Product);
    }

    [Fact]
    public async Task Hello_documents_and_readers()
    {
        await using var client = await Connect();
        var hello = await client.HelloAsync();
        Assert.Equal(Protocol.Version, hello.ProtocolVersion);
        Assert.Equal(Environment.ProcessId, hello.Host.ProcessId);

        var docs = await client.ListDocumentsAsync();
        Assert.Equal(new[] { "d1", "d2" }, docs.Select(d => d.Id));
        Assert.Equal(_host.MainThreadId, _docs.LastCallThread); // marshalled to the host thread

        var readers = await client.ListReadersAsync();
        Assert.Contains(readers, r => r.Id == "fake.rooms" && !r.IsImplemented);
    }

    [Fact]
    public async Task Read_runs_on_host_thread_with_options()
    {
        await using var client = await Connect();
        var result = await client.ReadAsync(new ReadRequest
        {
            DocumentId = "d2",
            ReaderId = "fake.sheets",
            Options = { ["count"] = "5" },
        });

        Assert.Equal("d2", result.DocumentId);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("A-003", result.Items[2].Key);
        Assert.Contains("fake warning", result.Warnings);
        Assert.Equal(_host.MainThreadId, _sheets.ReadThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, _sheets.ReadThread);
    }

    [Theory]
    [InlineData("fake.rooms", "d1", ErrorCodes.NotImplemented)]
    [InlineData("nope", "d1", ErrorCodes.ReaderNotFound)]
    [InlineData("fake.sheets", "zzz", ErrorCodes.DocumentNotFound)]
    [InlineData("fake.boom", "d1", ErrorCodes.InternalError)]
    public async Task Errors_are_structured(string reader, string doc, string code)
    {
        await using var client = await Connect();
        var ex = await Assert.ThrowsAsync<AgentRequestException>(() =>
            client.ReadAsync(new ReadRequest { ReaderId = reader, DocumentId = doc }));
        Assert.Equal(code, ex.Code);

        // The connection survives errors.
        await client.PingAsync();
    }

    [Fact]
    public async Task Busy_host_returns_HostBusy_and_skips_the_work()
    {
        await using var client = await Connect();
        _host.Paused = true;
        try
        {
            var ex = await Assert.ThrowsAsync<AgentRequestException>(() => client.ListDocumentsAsync());
            Assert.Equal(ErrorCodes.HostBusy, ex.Code);
        }
        finally
        {
            _host.Paused = false;
        }
        // Ping never needs the host thread.
        await client.PingAsync();
        var docs = await client.ListDocumentsAsync();
        Assert.Equal(2, docs.Count);
    }

    [Fact]
    public async Task Concurrent_requests_on_one_connection()
    {
        await using var client = await Connect();
        var tasks = Enumerable.Range(1, 10).Select(i => client.ReadAsync(new ReadRequest
        {
            ReaderId = "fake.sheets",
            DocumentId = "d1",
            Options = { ["count"] = i.ToString() },
        })).ToList();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(1, 10), results.Select(r => r.Items.Count));
    }

    [Fact]
    public async Task Protocol_mismatch_is_rejected()
    {
        await using var client = await Connect();
        var ex = await Assert.ThrowsAsync<AgentRequestException>(() =>
            client.RequestAsync<HelloResponse>("hello", new { }, TimeSpan.FromSeconds(5), default, protocolVersion: 99));
        Assert.Equal(ErrorCodes.ProtocolMismatch, ex.Code);
    }
}
