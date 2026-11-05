using Nexus.Agent;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class WriteTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "nexus-tests-" + Guid.NewGuid().ToString("N"));
    private AgentLog _log = null!;
    private FakeHost _host = null!;
    private FakeWriter _writer = null!;
    private AgentServer<FakeDoc> _writable = null!;
    private AgentServer<FakeDoc> _readOnly = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("NEXUS_HOME", _home);
        _log = new AgentLog("test");
        _host = new FakeHost(_log);
        _writer = new FakeWriter();
        _writable = Server(_writer);
        _readOnly = Server(null);
        return Task.CompletedTask;
    }

    private AgentServer<FakeDoc> Server(IHostDataWriter<FakeDoc>? writer)
    {
        var info = HostInfoFactory.Create("Fake", "FakeCAD", "2026", "test", _log);
        info.HostKind = "Fake" + Guid.NewGuid().ToString("N")[..8];
        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(new FakeSheetsReader());
        var server = new AgentServer<FakeDoc>(info, _host.Queue, new FakeDocs(), readers, _log,
            new AgentServerOptions { WriteRegistration = false }, writer);
        server.Start();
        return server;
    }

    public Task DisposeAsync()
    {
        _writable.Dispose();
        _readOnly.Dispose();
        _host.Dispose();
        try { Directory.Delete(_home, true); } catch { /* ignored */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Write_feature_is_advertised_only_with_a_writer()
    {
        await using var a = await AgentClient.ConnectAsync(_writable.PipeName, TimeSpan.FromSeconds(5));
        await using var b = await AgentClient.ConnectAsync(_readOnly.PipeName, TimeSpan.FromSeconds(5));
        Assert.Contains(AgentFeatures.Write, (await a.HelloAsync()).Host.Features);
        Assert.DoesNotContain(AgentFeatures.Write, (await b.HelloAsync()).Host.Features);
    }

    [Fact]
    public async Task Write_runs_on_host_thread_and_reports_each_change()
    {
        await using var client = await AgentClient.ConnectAsync(_writable.PipeName, TimeSpan.FromSeconds(5));
        var result = await client.WriteAsync(new WriteRequest
        {
            DocumentId = "d2",
            Changes =
            {
                new PropertyChange { OwnerId = "d2-1", PropertyId = "SHEET_NAME", PropertyName = "Sheet Name", Value = "plan x" },
                new PropertyChange { OwnerId = "d2-2", PropertyId = "SHEET_NAME", PropertyName = "Sheet Name", Value = "bad" },
            },
        });

        Assert.Equal("d2", result.DocumentId);
        Assert.Equal("Site.rvt", result.DocumentTitle);
        Assert.Equal(new[] { ChangeStatus.Applied, ChangeStatus.Failed }, result.Results.Select(r => r.Status));
        Assert.Equal("PLAN X", result.Results[0].NewValue);
        Assert.Equal(_host.MainThreadId, _writer.WriteThread);
        Assert.Equal(("d2", "d2-1"), (_writer.Applied.Single().Doc, _writer.Applied.Single().Change.OwnerId));
    }

    [Fact]
    public async Task Disposing_a_client_twice_at_once_does_not_throw()
    {
        var client = await AgentClient.ConnectAsync(_writable.PipeName, TimeSpan.FromSeconds(5));
        await client.HelloAsync();
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask(), Task.Run(async () => await client.DisposeAsync()));
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData(false, "d1", ErrorCodes.NotImplemented)]
    [InlineData(true, "zzz", ErrorCodes.DocumentNotFound)]
    [InlineData(true, "", ErrorCodes.BadRequest)]
    public async Task Write_errors_are_structured(bool writable, string doc, string code)
    {
        await using var client = await AgentClient.ConnectAsync((writable ? _writable : _readOnly).PipeName, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<AgentRequestException>(() => client.WriteAsync(new WriteRequest
        {
            DocumentId = doc,
            Changes = { new PropertyChange { OwnerId = "x", PropertyName = "Sheet Name", Value = "y" } },
        }));
        Assert.Equal(code, ex.Code);
    }
}

public class EditingTests
{
    private static HostInfo Host(int pid, bool canWrite) => new()
    {
        Product = "Revit", Version = "2026", ProcessId = pid,
        Features = canWrite ? new() { AgentFeatures.Write } : new(),
    };

    private static ResultTable Table(HostInfo host, string docId, params DataItem[] items) =>
        ResultTable.Build(new[]
        {
            new ResultSource
            {
                Host = host, DocumentTitle = docId,
                Result = new ReadResult { ReaderId = "revit.sheets", DocumentId = docId, Items = items.ToList() },
            },
        });

    private static DataItem Sheet(string id, params PropertyValue[] props) => new()
    {
        Id = id, Name = id, ItemType = "Sheet",
        Groups = { new PropertyGroup("Identity Data") { Properties = props.ToList() } },
    };

    private static PropertyValue P(string name, string value, string? owner = null) => new()
    {
        Name = name, Id = name.ToUpperInvariant(), Value = value, RawValue = value,
        Source = PropertySource.BuiltIn, StorageType = "String", OwnerId = owner,
    };

    [Fact]
    public void Blocker_explains_why_a_cell_is_locked()
    {
        var locked = P("Drawn By", "WS");
        locked.IsReadOnly = true;
        locked.ReadOnlyReason = "Borrowed by Bob";
        var elementRef = P("Material", "Concrete");
        elementRef.StorageType = "ElementId";
        var derived = P("Workset", "Sheets");
        derived.Source = PropertySource.Derived;

        var t = Table(Host(1, true), "d1", Sheet("s1", P("Sheet Name", "Plan"), locked, elementRef, derived));
        var row = t.Rows[0];
        Assert.Null(Editing.Blocker(row, "Identity Data › Sheet Name"));
        Assert.Equal("Borrowed by Bob", Editing.Blocker(row, "Identity Data › Drawn By"));
        Assert.Contains("refer to other objects", Editing.Blocker(row, "Identity Data › Material"));
        Assert.Contains("Computed", Editing.Blocker(row, "Identity Data › Workset"));
        Assert.NotNull(Editing.Blocker(row, "Identity Data › Nope"));

        var readOnlyHost = Table(Host(2, false), "d1", Sheet("s1", P("Sheet Name", "Plan")));
        Assert.Contains("does not support editing", Editing.Blocker(readOnlyHost.Rows[0], "Identity Data › Sheet Name"));
    }

    [Fact]
    public void Plan_groups_by_document_targets_owner_and_keeps_last_duplicate()
    {
        var a = Table(Host(1, true), "d1",
            Sheet("s1", P("Sheet Name", "Plan"), P("Title Block Scale", "1:50", owner: "tb1")),
            Sheet("s2", P("Sheet Name", "Section")));
        var b = Table(Host(2, true), "d9", Sheet("s9", P("Sheet Name", "Site")));

        var edits = new[]
        {
            new CellEdit(a.Rows[0], "Identity Data › Sheet Name", "Plan A"),
            new CellEdit(a.Rows[0], "Identity Data › Title Block Scale", "1:100"),
            new CellEdit(a.Rows[1], "Identity Data › Sheet Name", "Section B"),
            new CellEdit(b.Rows[0], "Identity Data › Sheet Name", "Site C"),
            new CellEdit(a.Rows[0], "Identity Data › Sheet Name", "Plan Final"),
        };

        var plan = Editing.Plan(edits);
        Assert.Equal(2, plan.Count);

        var d1 = plan.Single(p => p.ProcessId == 1);
        Assert.Equal("d1", d1.Request.DocumentId);
        Assert.Equal(3, d1.Request.Changes.Count);
        var name = d1.Request.Changes.Single(c => c.OwnerId == "s1" && c.PropertyName == "Sheet Name");
        Assert.Equal("Plan Final", name.Value);
        Assert.Equal("Plan", name.ExpectedRawValue);
        Assert.Equal("SHEET NAME", name.PropertyId);
        Assert.Contains(d1.Request.Changes, c => c.OwnerId == "tb1" && c.Value == "1:100");
        Assert.Equal(d1.Request.Changes.Count, d1.Edits.Count);

        var d9 = plan.Single(p => p.ProcessId == 2);
        Assert.Equal("Site C", d9.Request.Changes.Single().Value);
    }

    [Theory]
    [InlineData(null, "", true)]
    [InlineData("a", "a", true)]
    [InlineData("a", "A", false)]
    public void SameValue_treats_null_as_empty(string? a, string? b, bool same) =>
        Assert.Equal(same, Editing.SameValue(a, b));
}
