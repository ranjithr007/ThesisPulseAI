using ThesisPulse.Shared.Contracts.Execution.V1;

namespace ThesisPulse.Shared.Infrastructure.Execution;

public interface IPaperExecutionLedgerStore
{
    Task<ExecutionCommandResultV1?> FindAuthorizationAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<ExecutionCommandResultV1> PersistAuthorizationAsync(
        ExecutionCommandResultV1 result,
        CancellationToken cancellationToken = default);

    Task<PaperOrderSnapshotV1?> GetOrderAsync(
        Guid paperOrderUid,
        CancellationToken cancellationToken = default);

    Task<PaperOrderTransitionResultV1> ApplyEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        Func<PaperOrderSnapshotV1, PaperOrderTransitionResultV1> transition,
        CancellationToken cancellationToken = default);
}

public sealed class PaperExecutionIdempotencyConflictException : Exception
{
    public PaperExecutionIdempotencyConflictException(string message)
        : base(message)
    {
    }
}

public sealed class PaperExecutionConcurrencyException : Exception
{
    public PaperExecutionConcurrencyException(string message)
        : base(message)
    {
    }
}
