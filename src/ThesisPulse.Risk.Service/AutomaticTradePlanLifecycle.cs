using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public static class AutomaticTradePlanLifecycleStatus
{
    public const string NotStarted = "NOT_STARTED";
    public const string Building = "PLAN_BUILDING";
    public const string Ready = "PLAN_READY";
    public const string Rejected = "PLAN_REJECTED";
    public const string RetryPending = "PLAN_RETRY_PENDING";
    public const string Expired = "PLAN_EXPIRED";
    public const string Cancelled = "PLAN_CANCELLED";
    public const string Failed = "PLAN_FAILED";

    public static bool IsTerminal(string status) => status is Ready or Rejected or Expired or Cancelled or Failed;
}

public static class AutomaticTradePlanTransitionMatrix
{
    public static bool CanTransition(string current, string next) => (current, next) switch
    {
        (AutomaticTradePlanLifecycleStatus.NotStarted, AutomaticTradePlanLifecycleStatus.Building) => true,
        (AutomaticTradePlanLifecycleStatus.NotStarted, AutomaticTradePlanLifecycleStatus.Rejected) => true,
        (AutomaticTradePlanLifecycleStatus.NotStarted, AutomaticTradePlanLifecycleStatus.Expired) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.Ready) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.Rejected) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.RetryPending) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.Expired) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.Cancelled) => true,
        (AutomaticTradePlanLifecycleStatus.Building, AutomaticTradePlanLifecycleStatus.Failed) => true,
        (AutomaticTradePlanLifecycleStatus.RetryPending, AutomaticTradePlanLifecycleStatus.Building) => true,
        (AutomaticTradePlanLifecycleStatus.RetryPending, AutomaticTradePlanLifecycleStatus.Expired) => true,
        (AutomaticTradePlanLifecycleStatus.RetryPending, AutomaticTradePlanLifecycleStatus.Cancelled) => true,
        (AutomaticTradePlanLifecycleStatus.RetryPending, AutomaticTradePlanLifecycleStatus.Failed) => true,
        _ => false,
    };
}

public sealed record AutomaticTradePlanStatusEvent(
    int Sequence,
    string Status,
    IReadOnlyCollection<string> Reasons,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid SourceMessageUid);

public sealed record AutomaticTradePlanLifecycleSnapshot(
    Guid RiskDecisionUid,
    Guid SourceMessageUid,
    string Status,
    TradePlanBuildResultV1? Result,
    IReadOnlyCollection<AutomaticTradePlanStatusEvent> Events,
    bool Duplicate);

public interface IAutomaticTradePlanLifecycleStore
{
    AutomaticTradePlanLifecycleSnapshot Read(Guid riskDecisionUid, Guid sourceMessageUid);
    AutomaticTradePlanLifecycleSnapshot Transition(
        Guid riskDecisionUid,
        Guid sourceMessageUid,
        string correlationId,
        string nextStatus,
        IReadOnlyCollection<string> reasons,
        TradePlanBuildResultV1? result,
        DateTimeOffset occurredAtUtc);
}

public sealed class InMemoryAutomaticTradePlanLifecycleStore : IAutomaticTradePlanLifecycleStore
{
    private sealed class State
    {
        public readonly object Gate = new();
        public string Status = AutomaticTradePlanLifecycleStatus.NotStarted;
        public TradePlanBuildResultV1? Result;
        public readonly List<AutomaticTradePlanStatusEvent> Events = new();
    }

    private readonly ConcurrentDictionary<Guid, State> _byRiskDecision = new();
    private readonly ConcurrentDictionary<Guid, Guid> _messageToRiskDecision = new();

    public AutomaticTradePlanLifecycleSnapshot Read(Guid riskDecisionUid, Guid sourceMessageUid)
    {
        if (!_byRiskDecision.TryGetValue(riskDecisionUid, out var state))
            return new(riskDecisionUid, sourceMessageUid, AutomaticTradePlanLifecycleStatus.NotStarted, null, Array.Empty<AutomaticTradePlanStatusEvent>(), false);

        lock (state.Gate)
            return Snapshot(riskDecisionUid, sourceMessageUid, state, false);
    }

    public AutomaticTradePlanLifecycleSnapshot Transition(
        Guid riskDecisionUid,
        Guid sourceMessageUid,
        string correlationId,
        string nextStatus,
        IReadOnlyCollection<string> reasons,
        TradePlanBuildResultV1? result,
        DateTimeOffset occurredAtUtc)
    {
        var mappedRiskDecision = _messageToRiskDecision.GetOrAdd(sourceMessageUid, riskDecisionUid);
        if (mappedRiskDecision != riskDecisionUid)
            throw new InvalidOperationException("Source message UID is already bound to another Risk decision.");

        var state = _byRiskDecision.GetOrAdd(riskDecisionUid, _ => new State());
        lock (state.Gate)
        {
            if (AutomaticTradePlanLifecycleStatus.IsTerminal(state.Status))
                return Snapshot(riskDecisionUid, sourceMessageUid, state, true);
            if (!AutomaticTradePlanTransitionMatrix.CanTransition(state.Status, nextStatus))
                throw new InvalidOperationException($"Illegal Trade Plan transition '{state.Status}' -> '{nextStatus}'.");

            state.Status = nextStatus;
            state.Result = result ?? state.Result;
            state.Events.Add(new AutomaticTradePlanStatusEvent(
                state.Events.Count,
                nextStatus,
                reasons,
                occurredAtUtc,
                correlationId,
                sourceMessageUid));
            return Snapshot(riskDecisionUid, sourceMessageUid, state, false);
        }
    }

    private static AutomaticTradePlanLifecycleSnapshot Snapshot(Guid riskDecisionUid, Guid sourceMessageUid, State state, bool duplicate) =>
        new(riskDecisionUid, sourceMessageUid, state.Status, state.Result, state.Events.ToArray(), duplicate);
}

public sealed class AutomaticTradePlanCoordinator(
    IAutomaticTradePlanProjector projector,
    ITradePlanBuilder builder,
    IAutomaticTradePlanLifecycleStore store)
{
    private readonly ConcurrentDictionary<Guid, object> _riskDecisionGates = new();

    public AutomaticTradePlanLifecycleSnapshot Build(AutomaticTradePlanIntakeV1 intake)
    {
        var riskDecisionUid = intake.RiskDecision.RiskDecisionUid;
        var gate = _riskDecisionGates.GetOrAdd(riskDecisionUid, _ => new object());
        lock (gate)
        {
            var existing = store.Read(riskDecisionUid, intake.MessageUid);
            if (AutomaticTradePlanLifecycleStatus.IsTerminal(existing.Status))
                return existing with { Duplicate = true };

            var projection = projector.Project(intake);
            if (projection.Command is null)
            {
                var terminalStatus = projection.Reasons.Any(reason => reason is "SIGNAL_EXPIRED" or "RISK_BUDGET_EXPIRED_OR_MISSING")
                    ? AutomaticTradePlanLifecycleStatus.Expired
                    : AutomaticTradePlanLifecycleStatus.Rejected;
                return store.Transition(
                    riskDecisionUid,
                    intake.MessageUid,
                    intake.CorrelationId,
                    terminalStatus,
                    projection.Reasons,
                    null,
                    intake.ReceivedAtUtc);
            }

            store.Transition(
                riskDecisionUid,
                intake.MessageUid,
                intake.CorrelationId,
                AutomaticTradePlanLifecycleStatus.Building,
                Array.Empty<string>(),
                null,
                intake.ReceivedAtUtc);
            var result = builder.Build(projection.Command.Request);
            var status = result.Status == TradePlanContractV1.Ready
                ? AutomaticTradePlanLifecycleStatus.Ready
                : AutomaticTradePlanLifecycleStatus.Rejected;
            return store.Transition(
                riskDecisionUid,
                intake.MessageUid,
                intake.CorrelationId,
                status,
                result.Reasons,
                result,
                result.EvaluatedAtUtc);
        }
    }
}
