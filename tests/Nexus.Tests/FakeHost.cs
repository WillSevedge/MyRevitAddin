using System.Collections.Concurrent;
using Nexus.Agent;
using Nexus.Contracts;

namespace Nexus.Tests;

/// <summary>A pretend host: its "main thread" is a dedicated thread draining a WorkQueue.</summary>
internal sealed class FakeHost : IDisposable
{
    private readonly BlockingCollection<bool> _signals = new();
    private readonly Thread _thread;
    public volatile bool Paused;

    public FakeHost(AgentLog log)
    {
        Queue = new WorkQueue(() => _signals.Add(true), log);
        _thread = new Thread(() =>
        {
            foreach (var _ in _signals.GetConsumingEnumerable())
            {
                MainThreadId = Environment.CurrentManagedThreadId;
                while (Paused) Thread.Sleep(10);
                Queue.Drain(TimeSpan.FromSeconds(1));
            }
        }) { IsBackground = true, Name = "fake-host-main" };
        _thread.Start();
    }

    public WorkQueue Queue { get; }
    public int MainThreadId { get; private set; }

    public void Dispose() => _signals.CompleteAdding();
}

internal sealed class FakeDoc
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
}

internal sealed class FakeDocs : IDocumentProvider<FakeDoc>
{
    public List<FakeDoc> Docs { get; } = new()
    {
        new FakeDoc { Id = "d1", Title = "Tower.rvt" },
        new FakeDoc { Id = "d2", Title = "Site.rvt" },
    };

    public int LastCallThread { get; private set; }

    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        LastCallThread = Environment.CurrentManagedThreadId;
        return Docs.Select((d, i) => new DocumentInfo { Id = d.Id, Title = d.Title, IsActive = i == 0 }).ToList();
    }

    public FakeDoc? Find(string documentId) =>
        documentId == "active" ? Docs.FirstOrDefault() : Docs.FirstOrDefault(d => d.Id == documentId);

    public DocumentInfo Describe(FakeDoc document) => new() { Id = document.Id, Title = document.Title };
}

internal sealed class FakeSheetsReader : IHostDataReader<FakeDoc>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "fake.sheets",
        DisplayName = "Sheets",
        Domain = "Sheets",
        Options = { ReaderOption.Int("count", "How many", 3) },
    };

    public int ReadThread { get; private set; }

    public void Read(FakeDoc document, ReadContext context)
    {
        ReadThread = Environment.CurrentManagedThreadId;
        for (int i = 1; i <= context.GetInt("count"); i++)
        {
            context.Items.Add(new DataItem
            {
                Id = $"{document.Id}-{i}",
                ItemType = "Sheet",
                Name = $"A-{i:000} - Plan {i}",
                Key = $"A-{i:000}",
                Groups =
                {
                    new PropertyGroup("Identity Data")
                    {
                        Properties =
                        {
                            new PropertyValue { Name = "Sheet Number", Value = $"A-{i:000}", Source = PropertySource.BuiltIn },
                            new PropertyValue { Name = "Sheet Name", Value = $"Plan {i}", Source = PropertySource.BuiltIn },
                            new PropertyValue { Name = "Drawn By", Value = "WS", Source = PropertySource.Shared, IsReadOnly = true, ReadOnlyReason = "Borrowed by Bob" },
                        },
                    },
                },
            });
        }
        context.Warn("fake warning");
    }
}

internal sealed class ThrowingReader : IHostDataReader<FakeDoc>
{
    public ReaderDescriptor Descriptor { get; } = new() { Id = "fake.boom", DisplayName = "Boom" };
    public void Read(FakeDoc document, ReadContext context) => throw new InvalidOperationException("kaboom");
}

/// <summary>Records edits; the value "bad" is rejected.</summary>
internal sealed class FakeWriter : IHostDataWriter<FakeDoc>
{
    public int WriteThread { get; private set; }
    public List<(string Doc, PropertyChange Change)> Applied { get; } = new();

    public WriteResult Write(FakeDoc document, WriteRequest request, AgentLog log, CancellationToken ct)
    {
        WriteThread = Environment.CurrentManagedThreadId;
        var result = new WriteResult { Committed = true, UndoName = "Fake edit" };
        for (int i = 0; i < request.Changes.Count; i++)
        {
            var c = request.Changes[i];
            if (c.Value == "bad")
            {
                result.Results.Add(new ChangeResult { Index = i, Status = ChangeStatus.Failed, Message = "not a number" });
                continue;
            }
            Applied.Add((document.Id, c));
            result.Results.Add(new ChangeResult { Index = i, Status = ChangeStatus.Applied, NewValue = c.Value.ToUpperInvariant() });
        }
        return result;
    }
}
