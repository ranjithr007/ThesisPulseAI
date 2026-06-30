namespace ThesisPulse.Shared.Contracts.Thesis.V1;

public static class ThesisFusionContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string CandidateStatus = "CANDIDATE";
}

public enum EvidenceDirectionV1
{
    Short = -1,
    Neutral = 0,
    Long = 1,
}

public sealed record DirectionalEvidenceV1(
    string EngineCode,
    string EngineVersion,
    string Timeframe,
    EvidenceDirectionV1 Direction,
    decimal Score,
    decimal Confidence,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record RegimeEvidenceV1(
    string RegimeCode,
    string EngineVersion,
    EvidenceDirectionV1 DirectionalBias,
    decimal Confidence,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record TimeframeConfirmationV1(
    string Timeframe,
    EvidenceDirectionV1 Direction,
    decimal Score,
    decimal Confidence,
    bool IsClosedCandle,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<string> Reasons);

public sealed record ThesisFusionRequestV1(
    Guid RequestUid,
    string CorrelationId,
    string InstrumentKey,
    string PrimaryTimeframe,
    DateTimeOffset AsOfUtc,
    string WeightConfigurationVersion,
    IReadOnlyCollection<DirectionalEvidenceV1> DirectionalEvidence,
    RegimeEvidenceV1 Regime,
    IReadOnlyCollection<TimeframeConfirmationV1> TimeframeConfirmations);

public sealed record ThesisEvidenceV1(
    string Source,
    string Timeframe,
    EvidenceDirectionV1 Direction,
    decimal RawScore,
    decimal Confidence,
    decimal AppliedWeight,
    decimal Contribution,
    IReadOnlyCollection<string> Reasons);

public sealed record CanonicalCandidateSignalV1(
    Guid SignalUid,
    string Status,
    string InstrumentKey,
    EvidenceDirectionV1 Direction,
    string PrimaryTimeframe,
    decimal Strength,
    decimal Confidence,
    DateTimeOffset GeneratedAtUtc,
    string FusionPolicyVersion,
    Guid ThesisUid);

public sealed record ThesisFusionResultV1(
    Guid ThesisUid,
    Guid RequestUid,
    string CorrelationId,
    string InstrumentKey,
    string Decision,
    EvidenceDirectionV1 Direction,
    decimal LongScore,
    decimal ShortScore,
    decimal Confidence,
    string Summary,
    IReadOnlyCollection<string> GateFailures,
    IReadOnlyCollection<ThesisEvidenceV1> Evidence,
    CanonicalCandidateSignalV1? Candidate,
    string EngineVersion,
    string WeightConfigurationVersion,
    DateTimeOffset GeneratedAtUtc);
