using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public static class AutomaticPortfolioRiskStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Evaluated = "EVALUATED";
    public const string Duplicate = "DUPLICATE";
    public const string RetryPending = "RETRY_PENDING";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
}

public sealed record AutomaticPortfolioRiskCandidate(
    Guid RequestUid,
    Guid SourcePnlSnapshotUid,
    Guid PolicyUid,
    string PolicyVersion,
    string PortfolioCode,
    string Environment,
    DateTimeOffset SourceAsOfUtc);

public sealed record AutomaticPortfolioRiskWorkItem(
    long WorkItemId,
    Guid RequestUid,
    Guid SourcePnlSnapshotUid,
    Guid PolicyUid,
    string PolicyVersion,
    string PortfolioCode,
    string Environment,
    DateTimeOffset SourceAsOfUtc,
    int AttemptCount);

public sealed record AutomaticPortfolioRiskEnqueueResult(
    string Outcome,
    Guid RequestUid,
    IReadOnlyCollection<string> Reasons);

public sealed record PortfolioRiskPersistResult(
    string Outcome,
    Guid RiskSnapshotUid,
    long ControlStateVersion,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPortfolioRiskWorkQueue
{
    Task<AutomaticPortfolioRiskEnqueueResult> EnqueueAsync(
        AutomaticPortfolioRiskCandidate candidate,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPortfolioRiskWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long workItemId,
        string resultStatus,
        Guid riskSnapshotUid,
        CancellationToken cancellationToken);

    Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken);

    Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken);
}

public interface IPortfolioRiskSnapshotStore
{
    Task<PortfolioRiskPersistResult> PersistAsync(
        PortfolioRiskSnapshotV1 snapshot,
        CancellationToken cancellationToken);
}
