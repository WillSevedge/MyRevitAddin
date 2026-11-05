using Nexus.Contracts;

namespace Nexus.Agent;

public sealed class ReaderRegistry<TDoc> where TDoc : class
{
    private readonly List<IHostDataReader<TDoc>> _readers = new();
    private readonly object _gate = new();

    public void Register(IHostDataReader<TDoc> reader)
    {
        lock (_gate)
        {
            if (_readers.Any(r => r.Descriptor.Id == reader.Descriptor.Id))
                throw new InvalidOperationException($"Reader '{reader.Descriptor.Id}' is already registered.");
            _readers.Add(reader);
        }
    }

    public IHostDataReader<TDoc>? Find(string id)
    {
        lock (_gate) return _readers.FirstOrDefault(r => string.Equals(r.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public List<ReaderDescriptor> Descriptors()
    {
        lock (_gate) return _readers.Select(r => r.Descriptor).ToList();
    }
}

/// <summary>
/// Registered-but-unimplemented reader. Shows up in the hub, returns NotImplemented.
/// </summary>
public sealed class PlaceholderReader<TDoc> : IHostDataReader<TDoc> where TDoc : class
{
    public PlaceholderReader(string id, string displayName, string domain, string description)
    {
        Descriptor = new ReaderDescriptor
        {
            Id = id,
            DisplayName = displayName + " (not implemented)",
            Domain = domain,
            Description = description,
            IsImplemented = false,
        };
    }

    public ReaderDescriptor Descriptor { get; }

    public void Read(TDoc document, ReadContext context) =>
        throw new AgentException(ErrorCodes.NotImplemented, $"Reader '{Descriptor.Id}' is not implemented yet.");
}
