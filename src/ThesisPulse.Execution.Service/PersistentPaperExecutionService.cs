using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public sealed class PersistentPaperExecutionService : IPaperExecutionService
{
    private static readonly IReadOnlySet<string> TerminalStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            PaperOrderStateContractV1.Filled,
            PaperOrderStateContractV1.Cancelled,
            PaperOrderStateContractV1.Rejected,
            PaperOrderStateContractV1.Expired,
        };

    private readonly DeterministicPaperExecutionService _gate;
    private readonly IPaperExecutionLedgerStore _store;

    public PersistentPaperExecutionService(
        DeterministicPaperExecutionService gate,
        IPaperExecutionLedgerStore store)
    {
        _gate = gate;
        _store = store;
    }

    public ExecutionCommandResultV1 Authorize(ExecutionCommandRequestV1 request)
    {
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = _store.FindAuthorizationAsync(idempotencyKey)
                .GetAwaiter()
                .GetResult();
            if (existing is not null)
            {
                return IsSameAuthorization(existing, request)
                    ? existing
                    : RejectIdempotencyConflict(request, existing);
            }
        }

        var result = _gate.Authorize(request);
        if (result.Status != ExecutionCommandContractV1.Authorized)
        {
            return result;
        }

        try
        {
            return _store.PersistAuthorizationAsync(result)
                .GetAwaiter()
                .GetResult();
        }
        catch (PaperExecutionIdempotencyConflictException)
        {
            var winner = _store.FindAuthorizationAsync(idempotencyKey)
                .GetAwaiter()
                .GetResult();
            return winner is not null && IsSameAuthorization(winner, request)
                ? winner
                : RejectIdempotencyConflict(request, winner ?? result);
        }
    }

    public PaperOrderSnapshotV1? GetOrder(Guid paperOrderUid) =>
        _store.GetOrderAsync(paperOrderUid)
            .GetAwaiter()
            .GetResult();

    public PaperOrderTransitionResultV1 ApplyEvent(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request) =>
        _store.ApplyEventAsync(
                paperOrderUid,
                request,
                snapshot => Transition(snapshot, request))
            .GetAwaiter()
            .GetResult();

    private static bool IsSameAuthorization(
        ExecutionCommandResultV1 existing,
        ExecutionCommandRequestV1 request) =>
        existing.Command?.TradePlanUid == request.TradePlan.TradePlanUid &&
        string.Equals(
            existing.Command.CorrelationId,
            request.CorrelationId?.Trim(),
            StringComparison.Ordinal);

    private static ExecutionCommandResultV1 RejectIdempotencyConflict(
        ExecutionCommandRequestV1 request,
        ExecutionCommandResultV1 template)
    {
        var check = new ExecutionGateCheckV1(
            "IDEMPOTENCY_KEY_AVAILABLE",
            false,
            null,
            null,
            "The idempotency key is already bound to another trade plan or correlation.");
        return new ExecutionCommandResultV1(
            request.RequestUid,
            request.IdempotencyKey?.Trim() ?? string.Empty,
            ExecutionCommandContractV1.Rejected,
            [check.Code],
            [check],
            null,
            null,
            template.GateVersion,
            request.ExecutionPolicyVersion,
            request.AsOfUtc);
    }

    private static PaperOrderTransitionResultV1 Transition(
        PaperOrderSnapshotV1 snapshot,
        PaperOrderEventRequestV1 request)
    {
        var reasons = new List<string>();
        var eventType = request.EventType?.Trim().ToUpperInvariant() ?? string.Empty;

        if (request.EventUid == Guid.Empty)
        {
            reasons.Add("EVENT_ID_REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            reasons.Add("EVENT_TYPE_REQUIRED");
        }

        if (request.OccurredAtUtc < snapshot.UpdatedAtUtc)
        {
            reasons.Add("EVENT_TIME_ORDERING");
        }

        if (TerminalStates.Contains(snapshot.State))
        {
            reasons.Add("ORDER_ALREADY_TERMINAL");
        }

        if (reasons.Count > 0)
        {
            return RejectTransition(reasons, snapshot, request.OccurredAtUtc);
        }

        PaperOrderSnapshotV1? updated = eventType switch
        {
            PaperOrderEventContractV1.Submit => SimpleTransition(
                snapshot,
                [PaperOrderStateContractV1.Created],
                PaperOrderStateContractV1.Submitted,
                request,
                reasons),
            PaperOrderEventContractV1.Acknowledge => SimpleTransition(
                snapshot,
                [PaperOrderStateContractV1.Submitted],
                PaperOrderStateContractV1.Acknowledged,
                request,
                reasons),
            PaperOrderEventContractV1.Fill => ApplyFill(snapshot, request, reasons),
            PaperOrderEventContractV1.Cancel => TerminalTransition(
                snapshot,
                [
                    PaperOrderStateContractV1.Created,
                    PaperOrderStateContractV1.Submitted,
                    PaperOrderStateContractV1.Acknowledged,
                    PaperOrderStateContractV1.PartiallyFilled,
                ],
                PaperOrderStateContractV1.Cancelled,
                request,
                reasons),
            PaperOrderEventContractV1.Reject => TerminalTransition(
                snapshot,
                [
                    PaperOrderStateContractV1.Created,
                    PaperOrderStateContractV1.Submitted,
                ],
                PaperOrderStateContractV1.Rejected,
                request,
                reasons),
            PaperOrderEventContractV1.Expire => TerminalTransition(
                snapshot,
                [
                    PaperOrderStateContractV1.Created,
                    PaperOrderStateContractV1.Submitted,
                    PaperOrderStateContractV1.Acknowledged,
                    PaperOrderStateContractV1.PartiallyFilled,
                ],
                PaperOrderStateContractV1.Expired,
                request,
                reasons),
            _ => UnknownEvent(reasons),
        };

        return updated is null || reasons.Count > 0
            ? RejectTransition(reasons, snapshot, request.OccurredAtUtc)
            : new PaperOrderTransitionResultV1(
                true,
                false,
                Array.Empty<string>(),
                updated,
                request.OccurredAtUtc,
                eventType == PaperOrderEventContractV1.Fill
                    ? request.EventUid
                    : null);
    }

    private static PaperOrderSnapshotV1? SimpleTransition(
        PaperOrderSnapshotV1 snapshot,
        IReadOnlyCollection<string> allowedStates,
        string nextState,
        PaperOrderEventRequestV1 request,
        ICollection<string> reasons)
    {
        if (!allowedStates.Contains(snapshot.State, StringComparer.Ordinal))
        {
            reasons.Add("INVALID_STATE_TRANSITION");
            return null;
        }

        return snapshot with
        {
            State = nextState,
            Version = snapshot.Version + 1,
            UpdatedAtUtc = request.OccurredAtUtc,
            LastReason = request.Reason,
        };
    }

    private static PaperOrderSnapshotV1? TerminalTransition(
        PaperOrderSnapshotV1 snapshot,
        IReadOnlyCollection<string> allowedStates,
        string nextState,
        PaperOrderEventRequestV1 request,
        ICollection<string> reasons)
    {
        var updated = SimpleTransition(
            snapshot,
            allowedStates,
            nextState,
            request,
            reasons);
        return updated is null
            ? null
            : updated with { TerminalAtUtc = request.OccurredAtUtc };
    }

    private static PaperOrderSnapshotV1? ApplyFill(
        PaperOrderSnapshotV1 snapshot,
        PaperOrderEventRequestV1 request,
        ICollection<string> reasons)
    {
        if (snapshot.State is not PaperOrderStateContractV1.Acknowledged and
            not PaperOrderStateContractV1.PartiallyFilled)
        {
            reasons.Add("INVALID_STATE_TRANSITION");
            return null;
        }

        var fillQuantity = request.FillQuantity ?? 0m;
        var fillPrice = request.FillPrice ?? 0m;
        if (fillQuantity <= 0)
        {
            reasons.Add("FILL_QUANTITY_POSITIVE");
        }

        if (fillPrice <= 0)
        {
            reasons.Add("FILL_PRICE_POSITIVE");
        }

        if (fillQuantity > snapshot.RemainingQuantity)
        {
            reasons.Add("FILL_EXCEEDS_REMAINING_QUANTITY");
        }

        if (!snapshot.AllowPartialFill && fillQuantity != snapshot.RemainingQuantity)
        {
            reasons.Add("PARTIAL_FILL_NOT_ALLOWED");
        }

        if (reasons.Count > 0)
        {
            return null;
        }

        var filledQuantity = snapshot.FilledQuantity + fillQuantity;
        var remainingQuantity = snapshot.RequestedQuantity - filledQuantity;
        var priorNotional = (snapshot.AverageFillPrice ?? 0m) * snapshot.FilledQuantity;
        var averageFillPrice = Math.Round(
            (priorNotional + fillPrice * fillQuantity) / filledQuantity,
            8);
        var filled = remainingQuantity == 0m;
        return snapshot with
        {
            State = filled
                ? PaperOrderStateContractV1.Filled
                : PaperOrderStateContractV1.PartiallyFilled,
            FilledQuantity = filledQuantity,
            RemainingQuantity = remainingQuantity,
            AverageFillPrice = averageFillPrice,
            Version = snapshot.Version + 1,
            UpdatedAtUtc = request.OccurredAtUtc,
            TerminalAtUtc = filled ? request.OccurredAtUtc : null,
            LastReason = request.Reason,
        };
    }

    private static PaperOrderSnapshotV1? UnknownEvent(ICollection<string> reasons)
    {
        reasons.Add("UNSUPPORTED_EVENT_TYPE");
        return null;
    }

    private static PaperOrderTransitionResultV1 RejectTransition(
        IReadOnlyCollection<string> reasons,
        PaperOrderSnapshotV1 snapshot,
        DateTimeOffset evaluatedAtUtc) =>
        new(
            false,
            false,
            reasons.Distinct(StringComparer.Ordinal).ToArray(),
            snapshot,
            evaluatedAtUtc);
}
