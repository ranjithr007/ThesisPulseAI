using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class FusionSignalProjectionContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Projected = "PROJECTED";
    public const string Rejected = "REJECTED";
}

public sealed record FusionSignalLineageV1(
    Guid ThesisUid,
    Guid ThesisRequestUid,
    Guid CandidateSignalUid,
    Guid FusionEvidenceUid,
    Guid SourceCandleMessageUid,
    Guid ConfirmationOutputUid,
    Guid ConfirmationMessageUid,
    string FusionEngineVersion,
    string FusionPolicyVersion,
    string WeightConfigurationVersion);

public sealed record FusionSignalProjectionRequestV1(
    FusionReadyEvidenceV1 FusionEvidence,
    ThesisFusionResultV1 Thesis,
    DateTimeOffset EntryOpensAtUtc,
    DateTimeOffset EntryClosesAtUtc,
    DateTimeOffset ValidUntilUtc,
    int ExpectedHoldingPeriodMinutes);

public sealed record FusionSignalIntakeV1(
    EventEnvelope<SignalGeneratedV1> Envelope,
    FusionSignalLineageV1 Lineage);

public sealed record FusionSignalProjectionResultV1(
    string Outcome,
    FusionSignalIntakeV1? Intake,
    IReadOnlyCollection<string> Reasons);
