using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Execution.Service;

public interface IPaperExecutionService
{
    ExecutionCommandResultV1 Authorize(ExecutionCommandRequestV1 request);

    PaperOrderSnapshotV1? GetOrder(Guid paperOrderUid);

    PaperOrderTransitionResultV1 ApplyEvent(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request);
}

public sealed class DeterministicPaperExecutionService : IPaperExecutionService
{
    private static readonly IReadOnlySet<string> TerminalStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            PaperOrderStateContractV1.Filled,
            PaperOrderStateContractV1.Cancelled,
            PaperOrderStateContractV1.Rejected,
            PaperOrderStateContractV1.Expired,
        };

    private readonly DeterministicPaperExecutionOptions _policy;
    private readonly ConcurrentDictionary<string, ExecutionCommandResultV1> _authorizedByIdempotency =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PaperOrderAggregate> _orders = new();

    public DeterministicPaperExecutionService(
        IOptions<DeterministicPaperExecutionOptions> options)
    {
        _policy = options.Value;
    }

    public ExecutionCommandResultV1 Authorize(ExecutionCommandRequestV1 request)
    {
        var now = request.AsOfUtc;
        var plan = request.TradePlan;
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        var correlationId = request.CorrelationId?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            _authorizedByIdempotency.TryGetValue(idempotencyKey, out var existing))
        {
            if (existing.Command?.TradePlanUid == plan.TradePlanUid &&
                string.Equals(existing.Command.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                return existing;
            }

            return Reject(
                request,
                [new ExecutionGateCheckV1(
                    "IDEMPOTENCY_KEY_AVAILABLE",
                    false,
                    null,
                    null,
                    "The idempotency key is already bound to a different trade plan or correlation.")],
                now);
        }

        var checks = new List<ExecutionGateCheckV1>();
        var targets = plan.Targets ?? Array.Empty<TradePlanTargetV1>();
        var entry = plan.Entry;
        var stopLoss = plan.StopLoss;
        var session = plan.Session;
        var exitPolicy = plan.ExitPolicy;

        AddCheck(
            checks,
            "REQUEST_IDENTITY",
            request.RequestUid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(idempotencyKey) &&
            !string.IsNullOrWhiteSpace(correlationId),
            null,
            null,
            "Request, idempotency and correlation identifiers are mandatory.");
        AddCheck(
            checks,
            "CORRELATION_LINEAGE",
            string.Equals(correlationId, plan.CorrelationId, StringComparison.Ordinal),
            null,
            null,
            "Execution request correlation must match the trade plan.");
        AddCheck(
            checks,
            "EXECUTION_POLICY_VERSION",
            string.Equals(request.ExecutionPolicyVersion, _policy.ExecutionPolicyVersion, StringComparison.Ordinal) &&
            string.Equals(plan.ExecutionPolicyVersion, _policy.ExecutionPolicyVersion, StringComparison.Ordinal),
            null,
            null,
            $"Request and plan must use active policy '{_policy.ExecutionPolicyVersion}'.");
        AddCheck(
            checks,
            "READY_TRADE_PLAN_REQUIRED",
            string.Equals(plan.Status, TradePlanContractV1.Ready, StringComparison.Ordinal),
            null,
            null,
            "Only a canonical READY trade plan can enter the execution command gate.");
        AddCheck(
            checks,
            "TRADE_PLAN_LINEAGE",
            plan.TradePlanUid != Guid.Empty &&
            plan.RiskDecisionUid != Guid.Empty &&
            plan.ThesisUid != Guid.Empty &&
            plan.SignalUid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(plan.InstrumentKey),
            null,
            null,
            "Trade plan, risk decision, thesis, signal and instrument lineage are mandatory.");
        AddCheck(
            checks,
            "UPSTREAM_NON_EXECUTABLE",
            !plan.ExecutionAuthorized,
            plan.ExecutionAuthorized ? 1 : 0,
            0,
            "The upstream Trade Plan Builder must not self-authorize execution.");
        AddCheck(
            checks,
            "PAPER_ENVIRONMENT_ONLY",
            string.Equals(plan.Environment, ExecutionCommandContractV1.PaperEnvironment, StringComparison.OrdinalIgnoreCase) &&
            _policy.AllowedEnvironments.Contains(plan.Environment, StringComparer.OrdinalIgnoreCase),
            null,
            null,
            "This slice authorizes PAPER execution commands only.");

        var expectedSide = plan.Direction switch
        {
            EvidenceDirectionV1.Long => "BUY",
            EvidenceDirectionV1.Short => "SELL",
            _ => string.Empty,
        };
        AddCheck(
            checks,
            "DIRECTION_SIDE_CONSISTENCY",
            !string.IsNullOrWhiteSpace(expectedSide) &&
            string.Equals(plan.Side, expectedSide, StringComparison.Ordinal),
            (decimal)plan.Direction,
            null,
            "LONG plans must use BUY and SHORT plans must use SELL.");

        AddCheck(
            checks,
            "TRADE_PLAN_CURRENT",
            now >= plan.GeneratedAtUtc && now < plan.ValidUntilUtc,
            (decimal)(plan.ValidUntilUtc - now).TotalSeconds,
            0,
            "Trade plan must be generated and unexpired.");
        AddCheck(
            checks,
            "ENTRY_WINDOW_OPEN",
            session is not null &&
            (session.NotBeforeUtc is null || now >= session.NotBeforeUtc) &&
            now < session.NewEntryCutoffUtc,
            null,
            null,
            "Execution authorization requires an open trade-plan entry window.");

        var operationsAgeSeconds = (decimal)(now - request.Operations.ObservedAtUtc).TotalSeconds;
        AddCheck(
            checks,
            "OPERATIONS_SNAPSHOT_FRESHNESS",
            operationsAgeSeconds >= 0 &&
            operationsAgeSeconds <= _policy.MaximumOperationalSnapshotAgeSeconds,
            operationsAgeSeconds,
            _policy.MaximumOperationalSnapshotAgeSeconds,
            "Execution operational state must be current.");
        AddCheck(
            checks,
            "KILL_SWITCH_CLEAR",
            !request.Operations.KillSwitchActive,
            null,
            null,
            "Kill switch blocks new execution commands.");
        AddCheck(
            checks,
            "TRADING_NOT_HALTED",
            !request.Operations.TradingHalted,
            null,
            null,
            "Operational trading halt blocks new execution commands.");
        AddCheck(
            checks,
            "MARKET_OPEN",
            request.Operations.MarketOpen,
            null,
            null,
            "New paper orders require an open market session.");
        AddCheck(
            checks,
            "MARKET_DATA_HEALTHY",
            request.Operations.MarketDataHealthy,
            null,
            null,
            "Unhealthy market data fails closed.");
        AddCheck(
            checks,
            "PAPER_GATEWAY_HEALTHY",
            request.Operations.PaperGatewayHealthy,
            null,
            null,
            "Unavailable paper gateway blocks authorization.");

        AddCheck(
            checks,
            "POSITIVE_QUANTITY",
            plan.ApprovedQuantity > 0,
            plan.ApprovedQuantity,
            0,
            "Approved execution quantity must be positive.");
        AddCheck(
            checks,
            "ENTRY_PRESENT",
            entry is not null && entry.ReferencePrice > 0,
            entry?.ReferencePrice,
            0,
            "A positive canonical entry definition is mandatory.");
        AddCheck(
            checks,
            "MANDATORY_STOP_PRESENT",
            stopLoss is not null && stopLoss.IsMandatory && stopLoss.Price > 0,
            stopLoss?.Price,
            0,
            "A positive mandatory stop-loss is required before command authorization.");

        var targetFractionSum = targets.Sum(target => target.QuantityFraction);
        AddCheck(
            checks,
            "TARGET_ENVELOPE",
            targets.Count > 0 &&
            targets.All(target => target.Price > 0 && target.QuantityFraction > 0) &&
            targetFractionSum == 1m,
            targetFractionSum,
            1m,
            "Targets must be positive and their quantity fractions must total exactly 1.0.");
        AddCheck(
            checks,
            "EXIT_POLICY_PRESENT",
            exitPolicy is not null && !string.IsNullOrWhiteSpace(exitPolicy.PolicyVersion),
            null,
            null,
            "A versioned exit policy is mandatory.");

        var reasons = checks
            .Where(check => !check.Passed)
            .Select(check => check.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (reasons.Length > 0 || session is null || entry is null || stopLoss is null || exitPolicy is null)
        {
            return Reject(request, checks, now);
        }

        var validUntilUtc = Min(
            plan.ValidUntilUtc,
            session.NewEntryCutoffUtc,
            now.AddSeconds(_policy.MaximumCommandValiditySeconds));
        if (validUntilUtc <= now)
        {
            checks.Add(new ExecutionGateCheckV1(
                "COMMAND_VALIDITY_EMPTY",
                false,
                0,
                0,
                "No positive execution-command validity window remains."));
            return Reject(request, checks, now);
        }

        var command = new ExecutionCommandV1(
            Guid.NewGuid(),
            request.RequestUid,
            plan.TradePlanUid,
            plan.RiskDecisionUid,
            plan.ThesisUid,
            plan.SignalUid,
            correlationId,
            idempotencyKey,
            ExecutionCommandContractV1.PaperEnvironment,
            plan.InstrumentKey,
            plan.Direction,
            expectedSide,
            plan.PositionIntent,
            entry,
            plan.ApprovedQuantity,
            plan.MinimumExecutionQuantity,
            plan.AllowPartialFill,
            stopLoss,
            targets.OrderBy(target => target.Sequence).ToArray(),
            plan.MaximumSlippageFraction,
            plan.TimeInForce,
            session,
            exitPolicy,
            request.ExecutionPolicyVersion,
            ExecutionCommandContractV1.Authorized,
            true,
            false,
            false,
            now,
            validUntilUtc);

        var paperOrder = new PaperOrderSnapshotV1(
            Guid.NewGuid(),
            command.ExecutionCommandUid,
            command.TradePlanUid,
            command.RiskDecisionUid,
            command.ThesisUid,
            command.SignalUid,
            command.CorrelationId,
            command.IdempotencyKey,
            command.Environment,
            command.InstrumentKey,
            command.Direction,
            command.Side,
            PaperOrderStateContractV1.Created,
            command.Quantity,
            0m,
            command.Quantity,
            null,
            command.AllowPartialFill,
            1,
            now,
            now,
            null,
            null);

        var result = new ExecutionCommandResultV1(
            request.RequestUid,
            idempotencyKey,
            ExecutionCommandContractV1.Authorized,
            Array.Empty<string>(),
            checks,
            command,
            paperOrder,
            _policy.GateVersion,
            request.ExecutionPolicyVersion,
            now);

        if (_authorizedByIdempotency.TryAdd(idempotencyKey, result))
        {
            _orders.TryAdd(paperOrder.PaperOrderUid, new PaperOrderAggregate(paperOrder));
            return result;
        }

        var winner = _authorizedByIdempotency[idempotencyKey];
        if (winner.Command?.TradePlanUid == plan.TradePlanUid &&
            string.Equals(winner.Command.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            return winner;
        }

        checks.Add(new ExecutionGateCheckV1(
            "IDEMPOTENCY_KEY_AVAILABLE",
            false,
            null,
            null,
            "The idempotency key was concurrently bound to a different trade plan."));
        return Reject(request, checks, now);
    }

    public PaperOrderSnapshotV1? GetOrder(Guid paperOrderUid)
    {
        if (!_orders.TryGetValue(paperOrderUid, out var aggregate))
        {
            return null;
        }

        lock (aggregate.SyncRoot)
        {
            return aggregate.Snapshot;
        }
    }

    public PaperOrderTransitionResultV1 ApplyEvent(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request)
    {
        if (!_orders.TryGetValue(paperOrderUid, out var aggregate))
        {
            return TransitionRejected(
                ["PAPER_ORDER_NOT_FOUND"],
                null,
                request.OccurredAtUtc);
        }

        lock (aggregate.SyncRoot)
        {
            if (request.EventUid != Guid.Empty && aggregate.AppliedEventUids.Contains(request.EventUid))
            {
                return new PaperOrderTransitionResultV1(
                    true,
                    true,
                    Array.Empty<string>(),
                    aggregate.Snapshot,
                    request.OccurredAtUtc);
            }

            var snapshot = aggregate.Snapshot;
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
                return TransitionRejected(reasons, snapshot, request.OccurredAtUtc);
            }

            PaperOrderSnapshotV1? updated = eventType switch
            {
                PaperOrderEventContractV1.Submit => ApplySimpleTransition(
                    snapshot,
                    [PaperOrderStateContractV1.Created],
                    PaperOrderStateContractV1.Submitted,
                    request,
                    reasons),
                PaperOrderEventContractV1.Acknowledge => ApplySimpleTransition(
                    snapshot,
                    [PaperOrderStateContractV1.Submitted],
                    PaperOrderStateContractV1.Acknowledged,
                    request,
                    reasons),
                PaperOrderEventContractV1.Fill => ApplyFill(snapshot, request, reasons),
                PaperOrderEventContractV1.Cancel => ApplyTerminalTransition(
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
                PaperOrderEventContractV1.Reject => ApplyTerminalTransition(
                    snapshot,
                    [
                        PaperOrderStateContractV1.Created,
                        PaperOrderStateContractV1.Submitted,
                    ],
                    PaperOrderStateContractV1.Rejected,
                    request,
                    reasons),
                PaperOrderEventContractV1.Expire => ApplyTerminalTransition(
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
                _ => AddUnknownEventReason(reasons),
            };

            if (updated is null || reasons.Count > 0)
            {
                return TransitionRejected(reasons, snapshot, request.OccurredAtUtc);
            }

            aggregate.Snapshot = updated;
            aggregate.AppliedEventUids.Add(request.EventUid);
            return new PaperOrderTransitionResultV1(
                true,
                false,
                Array.Empty<string>(),
                updated,
                request.OccurredAtUtc);
        }
    }

    private static PaperOrderSnapshotV1? ApplySimpleTransition(
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

    private static PaperOrderSnapshotV1? ApplyTerminalTransition(
        PaperOrderSnapshotV1 snapshot,
        IReadOnlyCollection<string> allowedStates,
        string nextState,
        PaperOrderEventRequestV1 request,
        ICollection<string> reasons)
    {
        var updated = ApplySimpleTransition(snapshot, allowedStates, nextState, request, reasons);
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

        var newFilledQuantity = snapshot.FilledQuantity + fillQuantity;
        var remainingQuantity = snapshot.RequestedQuantity - newFilledQuantity;
        var previousNotional = (snapshot.AverageFillPrice ?? 0m) * snapshot.FilledQuantity;
        var averageFillPrice = Math.Round(
            (previousNotional + fillPrice * fillQuantity) / newFilledQuantity,
            8);
        var filled = remainingQuantity == 0m;

        return snapshot with
        {
            State = filled
                ? PaperOrderStateContractV1.Filled
                : PaperOrderStateContractV1.PartiallyFilled,
            FilledQuantity = newFilledQuantity,
            RemainingQuantity = remainingQuantity,
            AverageFillPrice = averageFillPrice,
            Version = snapshot.Version + 1,
            UpdatedAtUtc = request.OccurredAtUtc,
            TerminalAtUtc = filled ? request.OccurredAtUtc : null,
            LastReason = request.Reason,
        };
    }

    private static PaperOrderSnapshotV1? AddUnknownEventReason(ICollection<string> reasons)
    {
        reasons.Add("UNSUPPORTED_EVENT_TYPE");
        return null;
    }

    private ExecutionCommandResultV1 Reject(
        ExecutionCommandRequestV1 request,
        IReadOnlyCollection<ExecutionGateCheckV1> checks,
        DateTimeOffset evaluatedAtUtc)
    {
        var reasons = checks
            .Where(check => !check.Passed)
            .Select(check => check.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ExecutionCommandResultV1(
            request.RequestUid,
            request.IdempotencyKey?.Trim() ?? string.Empty,
            ExecutionCommandContractV1.Rejected,
            reasons,
            checks,
            null,
            null,
            _policy.GateVersion,
            request.ExecutionPolicyVersion,
            evaluatedAtUtc);
    }

    private static PaperOrderTransitionResultV1 TransitionRejected(
        IReadOnlyCollection<string> reasons,
        PaperOrderSnapshotV1? snapshot,
        DateTimeOffset evaluatedAtUtc) =>
        new(
            false,
            false,
            reasons.Distinct(StringComparer.Ordinal).ToArray(),
            snapshot,
            evaluatedAtUtc);

    private static DateTimeOffset Min(params DateTimeOffset[] values) => values.Min();

    private static void AddCheck(
        ICollection<ExecutionGateCheckV1> checks,
        string code,
        bool passed,
        decimal? observedValue,
        decimal? limitValue,
        string detail) =>
        checks.Add(new ExecutionGateCheckV1(
            code,
            passed,
            observedValue,
            limitValue,
            detail));

    private sealed class PaperOrderAggregate
    {
        public PaperOrderAggregate(PaperOrderSnapshotV1 snapshot)
        {
            Snapshot = snapshot;
        }

        public object SyncRoot { get; } = new();

        public HashSet<Guid> AppliedEventUids { get; } = [];

        public PaperOrderSnapshotV1 Snapshot { get; set; }
    }
}
