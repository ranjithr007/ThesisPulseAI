namespace ThesisPulse.Shared.Contracts.Execution.V1;

public static class PaperTradeLifecycleContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string PaperEnvironment = "PAPER";

    public const string Complete = "COMPLETE";
    public const string InProgress = "IN_PROGRESS";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
    public const string PartialLineage = "PARTIAL_LINEAGE";
}

public sealed record PaperTradeLifecycleStageV1(
    string Stage,
    string Status,
    Guid? EntityUid,
    DateTimeOffset? OccurredAtUtc,
    string? ReasonCode,
    string? ReasonMessage);

public sealed record PaperTradeLifecycleSummaryV1(
    Guid CorrelationUid,
    string? PortfolioCode,
    string InstrumentKey,
    string StrategyCode,
    string Direction,
    string LifecycleStage,
    string LifecycleStatus,
    bool IsComplete,
    bool IsStale,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset ObservedAtUtc,
    Guid SignalUid,
    Guid? ThesisUid,
    Guid? RiskDecisionUid,
    Guid TradePlanUid,
    Guid? ExecutionCommandUid,
    Guid? OrderUid,
    int FillCount,
    Guid? PositionUid,
    Guid? PnlSnapshotUid,
    decimal? RequestedQuantity,
    decimal? FilledQuantity,
    decimal? AverageFillPrice,
    decimal? PositionQuantity,
    decimal? NetPnlAmount,
    IReadOnlyCollection<string> Warnings);

public sealed record PaperTradeLifecycleDetailV1(
    string ContractVersion,
    PaperTradeLifecycleSummaryV1 Summary,
    IReadOnlyCollection<PaperTradeLifecycleStageV1> Stages);

public sealed record PaperTradeLifecycleListV1(
    string ContractVersion,
    string Environment,
    string? PortfolioCode,
    int Limit,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<PaperTradeLifecycleSummaryV1> Items);
