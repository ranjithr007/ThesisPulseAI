using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Shared.Contracts.Execution.V1;

public static class ExecutionCommandContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Authorized = "AUTHORIZED";
    public const string Rejected = "REJECTED";
    public const string PaperEnvironment = "PAPER";
}

public static class PaperOrderStateContractV1
{
    public const string Created = "CREATED";
    public const string Submitted = "SUBMITTED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string PartiallyFilled = "PARTIALLY_FILLED";
    public const string Filled = "FILLED";
    public const string Cancelled = "CANCELLED";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
}

public static class PaperOrderEventContractV1
{
    public const string Submit = "SUBMIT";
    public const string Acknowledge = "ACKNOWLEDGE";
    public const string Fill = "FILL";
    public const string Cancel = "CANCEL";
    public const string Reject = "REJECT";
    public const string Expire = "EXPIRE";
}

public sealed record ExecutionOperationalStateV1(
    bool KillSwitchActive,
    bool TradingHalted,
    bool MarketOpen,
    bool MarketDataHealthy,
    bool PaperGatewayHealthy,
    DateTimeOffset ObservedAtUtc);

public sealed record ExecutionCommandRequestV1(
    Guid RequestUid,
    string IdempotencyKey,
    string CorrelationId,
    TradePlanV1 TradePlan,
    ExecutionOperationalStateV1 Operations,
    string ExecutionPolicyVersion,
    DateTimeOffset AsOfUtc);

public sealed record ExecutionGateCheckV1(
    string Code,
    bool Passed,
    decimal? ObservedValue,
    decimal? LimitValue,
    string Detail);

public sealed record ExecutionCommandV1(
    Guid ExecutionCommandUid,
    Guid RequestUid,
    Guid TradePlanUid,
    Guid RiskDecisionUid,
    Guid ThesisUid,
    Guid SignalUid,
    string CorrelationId,
    string IdempotencyKey,
    string Environment,
    string InstrumentKey,
    EvidenceDirectionV1 Direction,
    string Side,
    string PositionIntent,
    TradePlanEntryV1 Entry,
    decimal Quantity,
    decimal? MinimumExecutionQuantity,
    bool AllowPartialFill,
    TradePlanStopLossV1 StopLoss,
    IReadOnlyCollection<TradePlanTargetV1> Targets,
    decimal MaximumSlippageFraction,
    string TimeInForce,
    TradeSessionV1 Session,
    ExitPolicyV1 ExitPolicy,
    string ExecutionPolicyVersion,
    string Status,
    bool PaperSubmissionAuthorized,
    bool BrokerSubmissionAuthorized,
    bool LiveExecutionAuthorized,
    DateTimeOffset AuthorizedAtUtc,
    DateTimeOffset ValidUntilUtc);

public sealed record PaperOrderSnapshotV1(
    Guid PaperOrderUid,
    Guid ExecutionCommandUid,
    Guid TradePlanUid,
    Guid RiskDecisionUid,
    Guid ThesisUid,
    Guid SignalUid,
    string CorrelationId,
    string IdempotencyKey,
    string Environment,
    string InstrumentKey,
    EvidenceDirectionV1 Direction,
    string Side,
    string State,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal? AverageFillPrice,
    bool AllowPartialFill,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    string? LastReason);

public sealed record ExecutionCommandResultV1(
    Guid RequestUid,
    string IdempotencyKey,
    string Status,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<ExecutionGateCheckV1> Checks,
    ExecutionCommandV1? Command,
    PaperOrderSnapshotV1? PaperOrder,
    string GateVersion,
    string ExecutionPolicyVersion,
    DateTimeOffset EvaluatedAtUtc);

public sealed record PaperOrderEventRequestV1(
    Guid EventUid,
    string EventType,
    decimal? FillQuantity,
    decimal? FillPrice,
    string? Reason,
    DateTimeOffset OccurredAtUtc);

public sealed record PaperOrderTransitionResultV1(
    bool Applied,
    bool IdempotentReplay,
    IReadOnlyCollection<string> Reasons,
    PaperOrderSnapshotV1? PaperOrder,
    DateTimeOffset EvaluatedAtUtc);
