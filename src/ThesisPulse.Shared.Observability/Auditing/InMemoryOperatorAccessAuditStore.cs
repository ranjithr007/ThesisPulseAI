namespace ThesisPulse.Shared.Observability.Auditing;

public interface IOperatorAccessAuditStore
{
    void Record(OperatorAccessAuditEntry entry);

    IReadOnlyList<OperatorAccessAuditEntry> GetRecent(int requestedLimit);
}

public sealed class InMemoryOperatorAccessAuditStore : IOperatorAccessAuditStore
{
    private readonly OperatorAccessAuditOptions _options;
    private readonly object _gate = new();
    private readonly Queue<OperatorAccessAuditEntry> _entries = new();

    public InMemoryOperatorAccessAuditStore(OperatorAccessAuditOptions options)
    {
        _options = options;
        _options.Validate();
    }

    public void Record(OperatorAccessAuditEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _options.Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<OperatorAccessAuditEntry> GetRecent(int requestedLimit)
    {
        var limit = Math.Clamp(requestedLimit, 1, _options.MaximumReadLimit);
        lock (_gate)
        {
            return _entries
                .Reverse()
                .Take(limit)
                .ToArray();
        }
    }
}
