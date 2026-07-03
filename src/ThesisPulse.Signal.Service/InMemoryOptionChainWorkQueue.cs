namespace ThesisPulse.Signal.Service;

public sealed class InMemoryOptionChainWorkQueue : IOptionChainWorkQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OptionChainWorkItem> _items = new();

    public Task<bool> EnqueueAsync(OptionChainWorkItem workItem, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_items.ContainsKey(workItem.WorkUid))
                return Task.FromResult(false);
            _items.Add(workItem.WorkUid, workItem with
            {
                Status = OptionChainWorkStatus.Pending,
                AttemptCount = 0,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                TerminalReason = null,
            });
            return Task.FromResult(true);
        }
    }

    public Task<OptionChainWorkLease?> TryLeaseAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        lock (_gate)
        {
            var candidate = _items.Values
                .Where(item => item.AvailableAtUtc <= nowUtc)
                .Where(item => item.Status == OptionChainWorkStatus.Pending ||
                    (item.Status == OptionChainWorkStatus.Leased && item.LeaseExpiresAtUtc <= nowUtc))
                .OrderBy(item => item.AvailableAtUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.WorkUid)
                .FirstOrDefault();

            if (candidate is null)
                return Task.FromResult<OptionChainWorkLease?>(null);

            var expiresAt = nowUtc.Add(leaseDuration);
            var leased = candidate with
            {
                Status = OptionChainWorkStatus.Leased,
                AttemptCount = candidate.AttemptCount + 1,
                LeaseOwner = leaseOwner,
                LeaseExpiresAtUtc = expiresAt,
                UpdatedAtUtc = nowUtc,
            };
            _items[leased.WorkUid] = leased;
            return Task.FromResult<OptionChainWorkLease?>(new(leased, leaseOwner, expiresAt));
        }
    }

    public Task<bool> CompleteAsync(
        Guid workUid,
        string leaseOwner,
        OptionChainWorkStatus terminalStatus,
        string? terminalReason,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (terminalStatus is not (OptionChainWorkStatus.Completed or OptionChainWorkStatus.Duplicate or OptionChainWorkStatus.Rejected or OptionChainWorkStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(terminalStatus));

        lock (_gate)
        {
            if (!_items.TryGetValue(workUid, out var item) ||
                item.Status != OptionChainWorkStatus.Leased ||
                !string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal))
                return Task.FromResult(false);

            _items[workUid] = item with
            {
                Status = terminalStatus,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                TerminalReason = terminalReason,
                UpdatedAtUtc = completedAtUtc,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> RetryAsync(
        Guid workUid,
        string leaseOwner,
        DateTimeOffset availableAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(workUid, out var item) ||
                item.Status != OptionChainWorkStatus.Leased ||
                !string.Equals(item.LeaseOwner, leaseOwner, StringComparison.Ordinal))
                return Task.FromResult(false);

            _items[workUid] = item with
            {
                Status = OptionChainWorkStatus.Pending,
                AvailableAtUtc = availableAtUtc,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                TerminalReason = reason,
                UpdatedAtUtc = availableAtUtc,
            };
            return Task.FromResult(true);
        }
    }

    public Task<OptionChainWorkerMetrics> GetMetricsAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            long Count(OptionChainWorkStatus status) => _items.Values.LongCount(item => item.Status == status);
            var oldest = _items.Values
                .Where(item => item.Status == OptionChainWorkStatus.Pending)
                .Select(item => (DateTimeOffset?)item.CreatedAtUtc)
                .OrderBy(value => value)
                .FirstOrDefault();

            return Task.FromResult(new OptionChainWorkerMetrics(
                Count(OptionChainWorkStatus.Pending),
                Count(OptionChainWorkStatus.Leased),
                Count(OptionChainWorkStatus.Completed),
                Count(OptionChainWorkStatus.Duplicate),
                Count(OptionChainWorkStatus.Rejected),
                Count(OptionChainWorkStatus.Failed),
                _items.Values.LongCount(item => item.Status == OptionChainWorkStatus.Pending && item.AttemptCount > 0),
                oldest,
                observedAtUtc));
        }
    }
}
