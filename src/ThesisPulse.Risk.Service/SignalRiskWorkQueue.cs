using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed record SignalRiskWorkItem(
    long WorkItemId,
    Guid MessageUid,
    Guid SignalUid,
    SignalRiskEvaluationIntakeV1 Intake,
    int AttemptCount);

public interface ISignalRiskWorkQueue
{
    Task<IReadOnlyCollection<SignalRiskWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(long workItemId, CancellationToken cancellationToken);

    Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken);

    Task ExpireAsync(long workItemId, string reason, CancellationToken cancellationToken);

    Task FailAsync(long workItemId, string error, CancellationToken cancellationToken);
}
