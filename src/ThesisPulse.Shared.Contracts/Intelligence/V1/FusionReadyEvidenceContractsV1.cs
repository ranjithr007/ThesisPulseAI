namespace ThesisPulse.Shared.Contracts.Intelligence.V1;

public static class FusionReadyEvidenceContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Ready = "READY";
    public const string Ignored = "IGNORED";
    public const string Started = "STARTED";
    public const string Rejected = "REJECTED";
    public const string RetryPending = "RETRY_PENDING";
    public const string Failed = "FAILED";
}

public sealed record FusionDirectionalEvidenceV1(
    Guid OutputUid,
    string EngineCode,
    string EngineVersion,
    string Timeframe,
    string Direction,
    decimal Score,
    decimal Confidence,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record FusionRegimeEvidenceV1(
    Guid OutputUid,
    string RegimeCode,
    string EngineVersion,
    string Timeframe,
    string DirectionalBias,
    decimal Confidence,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record FusionTimeframeConfirmationV1(
    string Timeframe,
    Guid DirectionalOutputUid,
    Guid RegimeOutputUid,
    string Direction,
    decimal Score,
    decimal Confidence,
    bool IsClosedCandle,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record FusionTradeTargetProposalV1(
    int Sequence,
    decimal Price,
    decimal QuantityFraction);

public sealed record FusionTradeProposalV1(
    string Direction,
    decimal ReferencePrice,
    decimal MinimumAcceptablePrice,
    decimal MaximumAcceptablePrice,
    decimal StopLossPrice,
    IReadOnlyCollection<FusionTradeTargetProposalV1> Targets,
    decimal MaximumSlippageFraction,
    string ProposalPolicyVersion);

public sealed record FusionReadyEvidenceV1(
    Guid EvidenceUid,
    Guid SourceCandleMessageUid,
    Guid ConfirmationOutputUid,
    Guid ConfirmationMessageUid,
    string CorrelationId,
    string InstrumentKey,
    string PrimaryTimeframe,
    DateTimeOffset AsOfUtc,
    DateTimeOffset GeneratedAtUtc,
    string WeightConfigurationVersion,
    IReadOnlyCollection<FusionDirectionalEvidenceV1> DirectionalEvidence,
    FusionRegimeEvidenceV1 Regime,
    IReadOnlyCollection<FusionTimeframeConfirmationV1> TimeframeConfirmations,
    FusionTradeProposalV1 TradeProposal,
    bool IsEligibleForWorkflow,
    IReadOnlyCollection<string> Warnings);

public sealed record AutomaticPaperWorkflowIntakeResultV1(
    Guid EvidenceUid,
    string Status,
    IReadOnlyCollection<string> Reasons,
    Guid? WorkflowUid,
    string? WorkflowStatus,
    DateTimeOffset EvaluatedAtUtc);

public sealed record AiFeatureProcessingResponseV1(
    string Outcome,
    long StreamPosition,
    Guid MessageUid,
    FusionReadyEvidenceV1? WorkflowEvidence,
    string? Reason);
