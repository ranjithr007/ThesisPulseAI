namespace ThesisPulse.Signal.Service;

public enum OptionChainWorkStatus
{
    Pending,
    Leased,
    Completed,
    Duplicate,
    Rejected,
    Failed,
}

public sealed record OptionChainWorkItem(
    Guid WorkUid,
    Guid SnapshotUid,
    string InstrumentKey,
    DateTimeOffset WorkflowCutoffUtc,
    string EngineVersion,
    string PolicyVersion,
    OptionChainWorkStatus Status,
    int AttemptCount,
    DateTimeOffset AvailableAtUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? TerminalReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record OptionChainWorkerMetrics(
    long Pending,
    long Leased,
    long Completed,
    long Duplicate,
    long Rejected,
    long Failed,
    long RetryScheduled,
    DateTimeOffset? OldestPendingAtUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record OptionChainWorkLease(
    OptionChainWorkItem WorkItem,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAtUtc);

public interface IOptionChainWorkQueue
{
    Task<bool> EnqueueAsync(OptionChainWorkItem workItem, CancellationToken cancellationToken = default);

    Task<OptionChainWorkLease?> TryLeaseAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid workUid,
        string leaseOwner,
        OptionChainWorkStatus terminalStatus,
        string? terminalReason,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(
        Guid workUid,
        string leaseOwner,
        DateTimeOffset availableAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OptionChainWorkerMetrics> GetMetricsAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
