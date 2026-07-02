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

public sealed class AutomaticPortfolioRiskOptions
{
    public const string SectionName = "AutomaticPortfolioRisk";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 5;
    public int MaximumSourceAgeSeconds { get; init; } = 120;
    public string Environment { get; init; } = "PAPER";
    public string GlobalPolicyScopeId { get; init; } = "GLOBAL";

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPortfolioRisk:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("AutomaticPortfolioRisk:BatchSize must be between 1 and 500.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException("AutomaticPortfolioRisk:MaximumAttempts must be between 1 and 20.");
        if (MaximumSourceAgeSeconds is < 10 or > 3600)
            throw new InvalidOperationException("AutomaticPortfolioRisk:MaximumSourceAgeSeconds must be between 10 and 3600.");
        if (!string.Equals(Environment, "PAPER", StringComparison.Ordinal))
            throw new InvalidOperationException("Automatic portfolio risk is PAPER-only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(GlobalPolicyScopeId);
    }
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

public interface IAutomaticPortfolioRiskCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPortfolioRiskCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IPortfolioRiskEvaluationContextStore
{
    Task<PortfolioRiskEvaluationInputV1?> ReadAsync(
        AutomaticPortfolioRiskWorkItem workItem,
        CancellationToken cancellationToken);
}

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

public sealed class AutomaticPortfolioRiskProcessor(
    IPortfolioRiskEvaluationContextStore contextStore,
    IPortfolioRiskSnapshotStore snapshotStore,
    IAutomaticPortfolioRiskWorkQueue queue,
    AutomaticPortfolioRiskOptions options)
{
    public async Task ProcessAsync(
        AutomaticPortfolioRiskWorkItem workItem,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = await contextStore.ReadAsync(workItem, cancellationToken);
            if (input is null)
            {
                await queue.FailAsync(
                    workItem.WorkItemId,
                    "PORTFOLIO_RISK_CONTEXT_NOT_FOUND_OR_CHANGED",
                    cancellationToken);
                return;
            }

            if (DateTimeOffset.UtcNow - input.SourceAsOfUtc >
                TimeSpan.FromSeconds(options.MaximumSourceAgeSeconds))
            {
                await queue.FailAsync(
                    workItem.WorkItemId,
                    "PORTFOLIO_PNL_SNAPSHOT_STALE",
                    cancellationToken);
                return;
            }

            var snapshot = PortfolioRiskEvaluator.Evaluate(input);
            var persisted = await snapshotStore.PersistAsync(snapshot, cancellationToken);
            await queue.CompleteAsync(
                workItem.WorkItemId,
                persisted.Outcome,
                persisted.RiskSnapshotUid,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            workItem.AttemptCount < options.MaximumAttempts)
        {
            var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(6, workItem.AttemptCount)));
            await queue.RetryAsync(
                workItem.WorkItemId,
                exception.Message,
                DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await queue.FailAsync(workItem.WorkItemId, exception.Message, cancellationToken);
        }
    }
}
