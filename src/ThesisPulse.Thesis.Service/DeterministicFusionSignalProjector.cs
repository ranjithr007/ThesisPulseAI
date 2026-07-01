using System.Text;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Thesis.Service;

public sealed record FusionSignalProjectorOptions
{
    public const string SectionName = "FusionSignalProjection";

    public string Producer { get; init; } = "ThesisPulse.Thesis.Service";
    public string StrategyCode { get; init; } = "THESIS_FUSION";
    public string Environment { get; init; } = "PAPER";
}

public interface IFusionSignalProjector
{
    FusionSignalProjectionResultV1 Project(FusionSignalProjectionRequestV1 request);
}

public sealed class DeterministicFusionSignalProjector(
    FusionSignalProjectorOptions options) : IFusionSignalProjector
{
    private const decimal OneHundred = 100m;

    public FusionSignalProjectionResultV1 Project(FusionSignalProjectionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failures = Validate(request);
        if (failures.Count > 0)
        {
            return new FusionSignalProjectionResultV1(
                FusionSignalProjectionContractV1.Rejected,
                null,
                failures);
        }

        var thesis = request.Thesis;
        var fusion = request.FusionEvidence;
        var candidate = thesis.Candidate!;
        var direction = candidate.Direction == EvidenceDirectionV1.Long ? "LONG" : "SHORT";
        var messageUid = DeterministicGuidV1.Create(
            candidate.SignalUid,
            $"signal.generated.v1|{fusion.EvidenceUid:N}");
        var evidence = thesis.Evidence
            .Select((item, index) => new SignalEvidenceV1(
                EvidenceCode(item.Source, item.Timeframe, index + 1),
                EvidenceMessage(item),
                Impact(item.Direction, candidate.Direction),
                Math.Clamp(item.AppliedWeight, 0m, 1m)))
            .ToArray();
        var confirmationTimeframes = fusion.TimeframeConfirmations
            .Where(item => string.Equals(item.Direction, direction, StringComparison.Ordinal))
            .Select(item => item.Timeframe)
            .Append(candidate.PrimaryTimeframe)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeOrder)
            .ToArray();
        var payload = new SignalGeneratedV1(
            candidate.SignalUid,
            candidate.InstrumentKey,
            options.StrategyCode,
            thesis.EngineVersion,
            direction,
            candidate.PrimaryTimeframe,
            confirmationTimeframes,
            candidate.Strength / OneHundred,
            candidate.Confidence / OneHundred,
            request.EntryOpensAtUtc,
            request.EntryClosesAtUtc,
            fusion.TradeProposal.ReferencePrice,
            fusion.TradeProposal.MinimumAcceptablePrice,
            fusion.TradeProposal.MaximumAcceptablePrice,
            fusion.TradeProposal.StopLossPrice,
            "Fusion thesis invalidates at the deterministic protective stop.",
            request.ExpectedHoldingPeriodMinutes,
            thesis.GeneratedAtUtc,
            request.ValidUntilUtc,
            candidate.FusionPolicyVersion,
            evidence);
        var metadata = new MessageMetadata(
            messageUid,
            SignalContractV1.EventType,
            SignalContractV1.ContractVersion,
            thesis.GeneratedAtUtc,
            thesis.CorrelationId,
            fusion.EvidenceUid.ToString("D"),
            options.Producer,
            thesis.EngineVersion,
            options.Environment,
            thesis.WeightConfigurationVersion);
        var lineage = new FusionSignalLineageV1(
            thesis.ThesisUid,
            thesis.RequestUid,
            candidate.SignalUid,
            fusion.EvidenceUid,
            fusion.SourceCandleMessageUid,
            fusion.ConfirmationOutputUid,
            fusion.ConfirmationMessageUid,
            thesis.EngineVersion,
            candidate.FusionPolicyVersion,
            thesis.WeightConfigurationVersion);

        return new FusionSignalProjectionResultV1(
            FusionSignalProjectionContractV1.Projected,
            new FusionSignalIntakeV1(new EventEnvelope<SignalGeneratedV1>(metadata, payload), lineage),
            Array.Empty<string>());
    }

    private List<string> Validate(FusionSignalProjectionRequestV1 request)
    {
        var failures = new List<string>();
        var thesis = request.Thesis;
        var fusion = request.FusionEvidence;
        var candidate = thesis.Candidate;

        if (candidate is null || !string.Equals(
                thesis.Decision,
                ThesisFusionContractV1.CandidateStatus,
                StringComparison.Ordinal))
            failures.Add("THESIS_CANDIDATE_REQUIRED");
        if (thesis.GateFailures.Count > 0)
            failures.Add("THESIS_GATE_FAILURES_PRESENT");
        if (!fusion.IsEligibleForWorkflow)
            failures.Add("FUSION_EVIDENCE_NOT_ELIGIBLE");
        if (candidate is not null)
        {
            if (candidate.SignalUid == Guid.Empty || candidate.ThesisUid != thesis.ThesisUid)
                failures.Add("CANDIDATE_LINEAGE_INVALID");
            if (!string.Equals(candidate.InstrumentKey, fusion.InstrumentKey, StringComparison.Ordinal))
                failures.Add("INSTRUMENT_LINEAGE_MISMATCH");
            if (!string.Equals(candidate.PrimaryTimeframe, fusion.PrimaryTimeframe, StringComparison.Ordinal))
                failures.Add("TIMEFRAME_LINEAGE_MISMATCH");
            var expectedDirection = candidate.Direction == EvidenceDirectionV1.Long
                ? "LONG"
                : candidate.Direction == EvidenceDirectionV1.Short ? "SHORT" : "NEUTRAL";
            if (expectedDirection == "NEUTRAL" || !string.Equals(
                    expectedDirection,
                    fusion.TradeProposal.Direction,
                    StringComparison.Ordinal))
                failures.Add("DIRECTION_LINEAGE_MISMATCH");
            if (candidate.Strength is < 0 or > 100 || candidate.Confidence is < 0 or > 100)
                failures.Add("CANDIDATE_SCORE_INVALID");
        }
        if (thesis.ThesisUid == Guid.Empty || thesis.RequestUid == Guid.Empty ||
            fusion.EvidenceUid == Guid.Empty || fusion.SourceCandleMessageUid == Guid.Empty ||
            fusion.ConfirmationOutputUid == Guid.Empty || fusion.ConfirmationMessageUid == Guid.Empty)
            failures.Add("FUSION_LINEAGE_REQUIRED");
        if (!Guid.TryParse(thesis.CorrelationId, out _))
            failures.Add("CORRELATION_ID_INVALID");
        if (thesis.GeneratedAtUtc < fusion.AsOfUtc)
            failures.Add("FUSION_TIME_LINEAGE_INVALID");
        if (request.EntryOpensAtUtc < thesis.GeneratedAtUtc ||
            request.EntryClosesAtUtc <= request.EntryOpensAtUtc ||
            request.ValidUntilUtc <= thesis.GeneratedAtUtc ||
            request.ValidUntilUtc < request.EntryClosesAtUtc)
            failures.Add("SIGNAL_VALIDITY_WINDOW_INVALID");
        if (request.ExpectedHoldingPeriodMinutes < 1)
            failures.Add("HOLDING_PERIOD_INVALID");
        if (!string.Equals(options.Environment, "PAPER", StringComparison.Ordinal))
            failures.Add("PROJECTION_ENVIRONMENT_MUST_BE_PAPER");
        if (string.IsNullOrWhiteSpace(options.Producer) ||
            string.IsNullOrWhiteSpace(options.StrategyCode))
            failures.Add("PROJECTION_CONFIGURATION_INVALID");

        return failures.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string EvidenceCode(string source, string timeframe, int sequence)
    {
        var raw = $"FUSION_{sequence:D2}_{source}_{timeframe}".ToUpperInvariant();
        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.Length <= 100 ? builder.ToString() : builder.ToString(0, 100);
    }

    private static string EvidenceMessage(ThesisEvidenceV1 item)
    {
        var reasons = item.Reasons.Count == 0
            ? "No component reason supplied."
            : string.Join("; ", item.Reasons);
        return $"{item.Source} {item.Timeframe}: {reasons}";
    }

    private static string Impact(EvidenceDirectionV1 evidence, EvidenceDirectionV1 candidate) =>
        evidence == EvidenceDirectionV1.Neutral
            ? "NEUTRAL"
            : evidence == candidate
                ? candidate == EvidenceDirectionV1.Long ? "SUPPORTS_LONG" : "SUPPORTS_SHORT"
                : "CONTRADICTS";

    private static int TimeframeOrder(string timeframe) => timeframe switch
    {
        "1m" => 1,
        "5m" => 2,
        "15m" => 3,
        "1h" => 4,
        "1d" => 5,
        _ => 99,
    };
}
