using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Contracts.TradePlans.V1;

public static class TradePlanContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Ready = "READY";
    public const string Rejected = "REJECTED";
}

public sealed record TradeEntryProposalV1(
    string OrderType,
    decimal ReferencePrice,
    decimal? LimitPrice,
    decimal? TriggerPrice,
    decimal MinimumAcceptablePrice,
    decimal MaximumAcceptablePrice);

public sealed record TradeTargetProposalV1(
    int Sequence,
    decimal Price,
    decimal QuantityFraction);

public sealed record TradeSessionV1(
    DateOnly TradeDate,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset NewEntryCutoffUtc,
    DateTimeOffset MandatoryExitByUtc);

public sealed record ExitPolicyV1(
    bool AllowTrailingStop,
    bool AllowBreakEvenMove,
    bool AllowTimeExit,
    bool AllowSignalExit,
    string PolicyVersion);

public sealed record TradePlanBuildRequestV1(
    Guid RequestUid,
    string CorrelationId,
    RiskDecisionV1 RiskDecision,
    string PositionIntent,
    TradeEntryProposalV1 Entry,
    decimal StopLossPrice,
    string StopOrderType,
    decimal? StopLimitPrice,
    IReadOnlyCollection<TradeTargetProposalV1> Targets,
    decimal LotSize,
    decimal? RequestedQuantity,
    decimal? MinimumExecutionQuantity,
    bool AllowPartialFill,
    decimal MaximumSlippageFraction,
    string TimeInForce,
    TradeSessionV1 Session,
    ExitPolicyV1 ExitPolicy,
    string ExecutionPolicyVersion,
    DateTimeOffset AsOfUtc);

public sealed record TradePlanCheckV1(
    string Code,
    bool Passed,
    decimal? ObservedValue,
    decimal? LimitValue,
    string Detail);

public sealed record TradePlanEntryV1(
    string OrderType,
    decimal ReferencePrice,
    decimal? LimitPrice,
    decimal? TriggerPrice,
    decimal MinimumAcceptablePrice,
    decimal MaximumAcceptablePrice);

public sealed record TradePlanStopLossV1(
    decimal Price,
    string OrderType,
    decimal? LimitPrice,
    bool IsMandatory);

public sealed record TradePlanTargetV1(
    int Sequence,
    decimal Price,
    decimal QuantityFraction);

public sealed record TradePlanV1(
    Guid TradePlanUid,
    Guid RiskDecisionUid,
    Guid ThesisUid,
    Guid SignalUid,
    string CorrelationId,
    string Environment,
    string InstrumentKey,
    EvidenceDirectionV1 Direction,
    string Side,
    string PositionIntent,
    TradePlanEntryV1 Entry,
    decimal ApprovedQuantity,
    decimal? MinimumExecutionQuantity,
    bool AllowPartialFill,
    TradePlanStopLossV1 StopLoss,
    IReadOnlyCollection<TradePlanTargetV1> Targets,
    decimal MaximumSlippageFraction,
    string TimeInForce,
    TradeSessionV1 Session,
    ExitPolicyV1 ExitPolicy,
    decimal RiskAmountAtStop,
    decimal CapitalAtReference,
    decimal FirstTargetRiskReward,
    string ExecutionPolicyVersion,
    string Status,
    bool ExecutionAuthorized,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc);

public sealed record TradePlanBuildResultV1(
    Guid RequestUid,
    string CorrelationId,
    string Status,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<TradePlanCheckV1> Checks,
    TradePlanV1? TradePlan,
    string BuilderVersion,
    string ExecutionPolicyVersion,
    DateTimeOffset EvaluatedAtUtc);
