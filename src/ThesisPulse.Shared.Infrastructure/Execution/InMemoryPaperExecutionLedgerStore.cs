using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Execution.V1;

namespace ThesisPulse.Shared.Infrastructure.Execution;

public sealed class InMemoryPaperExecutionLedgerStore : IPaperExecutionLedgerStore
{
    private readonly ConcurrentDictionary<string, ExecutionCommandResultV1> _authorizations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, OrderAggregate> _orders = new();

    public Task<ExecutionCommandResultV1?> FindAuthorizationAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _authorizations.TryGetValue(idempotencyKey, out var result);
        return Task.FromResult(result);
    }

    public Task<ExecutionCommandResultV1> PersistAuthorizationAsync(
        ExecutionCommandResultV1 result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);

        var command = result.Command ?? throw new ArgumentException(
            "An authorized result must contain a command.",
            nameof(result));
        var order = result.PaperOrder ?? throw new ArgumentException(
            "An authorized result must contain a paper order.",
            nameof(result));

        if (_authorizations.TryGetValue(result.IdempotencyKey, out var existing))
        {
            if (existing.Command?.TradePlanUid == command.TradePlanUid &&
                string.Equals(
                    existing.Command.CorrelationId,
                    command.CorrelationId,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(existing);
            }

            throw new PaperExecutionIdempotencyConflictException(
                "The idempotency key is already bound to another trade plan.");
        }

        if (!_orders.TryAdd(order.PaperOrderUid, new OrderAggregate(order)))
        {
            throw new PaperExecutionIdempotencyConflictException(
                "The paper order UID already exists.");
        }

        if (!_authorizations.TryAdd(result.IdempotencyKey, result))
        {
            _orders.TryRemove(order.PaperOrderUid, out _);
            return PersistAuthorizationAsync(result, cancellationToken);
        }

        return Task.FromResult(result);
    }

    public Task<PaperOrderSnapshotV1?> GetOrderAsync(
        Guid paperOrderUid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_orders.TryGetValue(paperOrderUid, out var aggregate))
        {
            return Task.FromResult<PaperOrderSnapshotV1?>(null);
        }

        lock (aggregate.SyncRoot)
        {
            return Task.FromResult<PaperOrderSnapshotV1?>(aggregate.Snapshot);
        }
    }

    public Task<PaperOrderTransitionResultV1> ApplyEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        Func<PaperOrderSnapshotV1, PaperOrderTransitionResultV1> transition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transition);

        if (!_orders.TryGetValue(paperOrderUid, out var aggregate))
        {
            return Task.FromResult(new PaperOrderTransitionResultV1(
                false,
                false,
                ["PAPER_ORDER_NOT_FOUND"],
                null,
                request.OccurredAtUtc));
        }

        lock (aggregate.SyncRoot)
        {
            if (request.EventUid != Guid.Empty &&
                aggregate.AppliedEvents.TryGetValue(request.EventUid, out var replay))
            {
                return Task.FromResult(replay with
                {
                    Applied = true,
                    IdempotentReplay = true,
                    PaperOrder = aggregate.Snapshot,
                });
            }

            var result = transition(aggregate.Snapshot);
            if (!result.Applied || result.PaperOrder is null)
            {
                return Task.FromResult(result);
            }

            aggregate.Snapshot = result.PaperOrder;
            var persisted = result with
            {
                FillUid = string.Equals(
                    request.EventType,
                    PaperOrderEventContractV1.Fill,
                    StringComparison.OrdinalIgnoreCase)
                        ? request.EventUid
                        : null,
            };
            aggregate.AppliedEvents[request.EventUid] = persisted;
            return Task.FromResult(persisted);
        }
    }

    private sealed class OrderAggregate
    {
        public OrderAggregate(PaperOrderSnapshotV1 snapshot)
        {
            Snapshot = snapshot;
        }

        public object SyncRoot { get; } = new();

        public Dictionary<Guid, PaperOrderTransitionResultV1> AppliedEvents { get; } = [];

        public PaperOrderSnapshotV1 Snapshot { get; set; }
    }
}
