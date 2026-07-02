using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed record AutomaticTradePlanWorkItem(
    long WorkItemId,
    Guid SourceMessageUid,
    Guid CommandUid,
    Guid RiskDecisionUid,
    AutomaticTradePlanIntakeV1 Intake,
    int AttemptCount);

public sealed record AutomaticTradePlanEnqueueResult(
    string Outcome,
    Guid SourceMessageUid,
    Guid RiskDecisionUid,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticTradePlanWorkQueue
{
    Task<AutomaticTradePlanEnqueueResult> EnqueueAsync(
        AutomaticTradePlanIntakeV1 intake,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticTradePlanWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(long workItemId, CancellationToken cancellationToken);
    Task RetryAsync(long workItemId, string error, DateTimeOffset availableAtUtc, CancellationToken cancellationToken);
    Task RejectAsync(long workItemId, string reason, CancellationToken cancellationToken);
    Task ExpireAsync(long workItemId, string reason, CancellationToken cancellationToken);
    Task FailAsync(long workItemId, string error, CancellationToken cancellationToken);
}

public sealed class AutomaticTradePlanWorkerOptions
{
    public const string SectionName = "AutomaticTradePlanWorker";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 5;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("BatchSize must be between 1 and 500.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException("MaximumAttempts must be between 1 and 20.");
    }
}
