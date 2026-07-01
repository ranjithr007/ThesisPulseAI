using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Contracts.TradePlans.V1;

public static class AutomaticTradePlanContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Eligible = "ELIGIBLE";
    public const string Rejected = "REJECTED";
}

public sealed record TradePlanInstrumentContextV1(
    decimal LotSize,
    decimal? RequestedQuantity,
    decimal? MinimumExecutionQuantity,
    bool AllowPartialFill);

public sealed record TradePlanTargetPolicyV1(
    int Sequence,
    decimal RiskRewardMultiple,
    decimal QuantityFraction);

public sealed record TradePlanExecutionContextV1(
    string PositionIntent,
    string EntryOrderType,
    string StopOrderType,
    decimal? StopLimitPrice,
    decimal MaximumSlippageFraction,
    string TimeInForce,
    IReadOnlyCollection<TradePlanTargetPolicyV1> Targets,
    TradeSessionV1 Session,
    ExitPolicyV1 ExitPolicy,
    string ExecutionPolicyVersion);

public sealed record AutomaticTradePlanIntakeV1(
    Guid MessageUid,
    string CorrelationId,
    Guid? CausationMessageUid,
    RiskDecisionV1 RiskDecision,
    SignalGeneratedV1 Signal,
    TradePlanInstrumentContextV1 Instrument,
    TradePlanExecutionContextV1 Execution,
    DateTimeOffset ReceivedAtUtc);

public sealed record AutomaticTradePlanCommandV1(
    Guid CommandUid,
    Guid RequestUid,
    Guid SourceMessageUid,
    Guid? CausationMessageUid,
    Guid RiskDecisionUid,
    Guid SignalUid,
    Guid ThesisUid,
    string CorrelationId,
    TradePlanBuildRequestV1 Request,
    DateTimeOffset CreatedAtUtc);

public sealed record AutomaticTradePlanProjectionV1(
    Guid SourceMessageUid,
    string CorrelationId,
    string Outcome,
    IReadOnlyCollection<string> Reasons,
    AutomaticTradePlanCommandV1? Command,
    DateTimeOffset EvaluatedAtUtc);
