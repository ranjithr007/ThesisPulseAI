namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalScannerContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string RiskNotEvaluated = "NOT_EVALUATED";
    public const string RiskApproved = "APPROVED";
    public const string RiskRejected = "REJECTED";
    public const string RiskRestricted = "RESTRICTED";
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
    DateTimeOffset? RiskEvaluatedAtUtc);

public sealed record SignalScannerResultV1(
    DateTimeOffset AsOfUtc,
    IReadOnlyCollection<SignalScannerRowV1> Signals,
    int Count);
