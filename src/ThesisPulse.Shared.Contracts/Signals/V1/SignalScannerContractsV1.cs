namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalScannerContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string RiskNotEvaluated = "NOT_EVALUATED";
    public const string RiskApproved = "APPROVED";
    public const string RiskRejected = "REJECTED";
    public const string RiskRestricted = "RESTRICTED";
    public const string PlanNotAvailable = "NOT_AVAILABLE";
    public const string PlanPending = "PENDING";
    public const string PlanBuilding = "BUILDING";
    public const string PlanReady = "READY";
    public const string PlanRejected = "REJECTED";
    public const string PlanRetryPending = "RETRY_PENDING";
    public const string PlanExpired = "EXPIRED";
    public const string PlanCancelled = "CANCELLED";
    public const string PlanFailed = "FAILED";
}

public sealed record SignalScannerQueryV1(
    string? InstrumentKey,
    string? Direction,
    string? Status,
    decimal? MinimumConfidence,
    DateTimeOffset? GeneratedFromUtc,
    DateTimeOffset? GeneratedToUtc,
    bool ActiveOnly,
    int MaximumCount = 50);

public sealed record SignalTradePlanProjectionV1(
    string Status,
    Guid? TradePlanUid,
    decimal? ApprovedQuantity,
    decimal? EntryReferencePrice,
    decimal? StopLossPrice,
    DateTimeOffset? GeneratedAtUtc,
    DateTimeOffset? ValidUntilUtc,
    bool ExecutionAuthorized);

public sealed record SignalScannerRowV1(
    Guid SignalUid,
    Guid MessageUid,
    string InstrumentKey,
    string StrategyCode,
    string StrategyVersion,
    string Direction,
    string PrimaryTimeframe,
    decimal Strength,
    decimal Confidence,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    bool IsActive,
    string Producer,
    string CreatorEngineCode,
    Guid? ThesisUid,
    Guid? ThesisRequestUid,
    Guid? FusionEvidenceUid,
    Guid? SourceCandleMessageUid,
    Guid? ConfirmationOutputUid,
    Guid? ConfirmationMessageUid,
    string? FusionEngineVersion,
    string? FusionPolicyVersion,
    string? WeightConfigurationVersion,
    string RiskDecisionStatus,
    Guid? RiskDecisionUid,
    DateTimeOffset? RiskEvaluatedAtUtc,
    SignalTradePlanProjectionV1? TradePlan = null);

public sealed record SignalDecisionProjectionV1(
    Guid SignalUid,
    string RiskDecisionStatus,
    Guid? RiskDecisionUid,
    DateTimeOffset? RiskEvaluatedAtUtc,
    SignalTradePlanProjectionV1 TradePlan);

public sealed record SignalScannerResultV1(
    DateTimeOffset AsOfUtc,
    IReadOnlyCollection<SignalScannerRowV1> Signals,
    int Count);
