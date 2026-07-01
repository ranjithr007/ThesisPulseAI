using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Risk.Service;

public interface ISignalRiskProjector
{
    SignalRiskEvaluationProjectionResultV1 Project(SignalRiskEvaluationIntakeV1 intake);
}

public sealed class DeterministicSignalRiskProjector : ISignalRiskProjector
{
    private const decimal OneHundred = 100m;

    public SignalRiskEvaluationProjectionResultV1 Project(SignalRiskEvaluationIntakeV1 intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var failures = Validate(intake);
        if (failures.Count > 0)
        {
            return new SignalRiskEvaluationProjectionResultV1(
                SignalRiskEvaluationContractV1.Rejected,
                null,
                failures);
        }

        var direction = string.Equals(intake.Signal.Direction, "LONG", StringComparison.Ordinal)
            ? EvidenceDirectionV1.Long
            : EvidenceDirectionV1.Short;
        var requestUid = DeterministicGuidV1.Create(
            intake.Signal.SignalUid,
            $"risk-request.v1|{intake.RiskPolicyVersion}|{intake.Lineage.FusionPolicyVersion}");
        var commandUid = DeterministicGuidV1.Create(
            requestUid,
            $"{SignalRiskEvaluationContractV1.CommandType}|{intake.MessageUid:N}");
        var candidate = new CanonicalCandidateSignalV1(
            intake.Signal.SignalUid,
            ThesisFusionContractV1.CandidateStatus,
            intake.Signal.InstrumentKey,
            direction,
            intake.Signal.PrimaryTimeframe,
            Math.Round(intake.Signal.Strength * OneHundred, 8),
            Math.Round(intake.Signal.Confidence * OneHundred, 8),
            intake.Signal.GeneratedAtUtc,
            intake.Lineage.FusionPolicyVersion,
            intake.Lineage.ThesisUid);
        var request = new RiskDecisionRequestV1(
            requestUid,
            intake.CorrelationId,
            candidate,
            intake.Portfolio,
            intake.Operations,
            intake.RiskPolicyVersion,
            intake.AsOfUtc);
        var command = new SignalRiskEvaluationCommandV1(
            commandUid,
            requestUid,
            intake.MessageUid,
            intake.CorrelationId,
            intake.CausationMessageUid,
            intake.Signal.SignalUid,
            intake.Lineage.ThesisUid,
            request,
            intake.Lineage,
            SignalRiskEvaluationContractV1.ContractVersion,
            intake.AsOfUtc);

        return new SignalRiskEvaluationProjectionResultV1(
            SignalRiskEvaluationContractV1.Eligible,
            command,
            Array.Empty<string>());
    }

    private static List<string> Validate(SignalRiskEvaluationIntakeV1 intake)
    {
        var failures = new List<string>();
        var signal = intake.Signal;
        var lineage = intake.Lineage;

        if (intake.MessageUid == Guid.Empty)
            failures.Add("SOURCE_MESSAGE_UID_REQUIRED");
        if (!Guid.TryParse(intake.CorrelationId, out _))
            failures.Add("CORRELATION_ID_INVALID");
        if (signal.SignalUid == Guid.Empty || lineage.ThesisUid == Guid.Empty ||
            lineage.ThesisRequestUid == Guid.Empty || lineage.FusionEvidenceUid == Guid.Empty ||
            lineage.SourceCandleMessageUid == Guid.Empty || lineage.ConfirmationOutputUid == Guid.Empty ||
            lineage.ConfirmationMessageUid == Guid.Empty)
            failures.Add("SIGNAL_FUSION_LINEAGE_REQUIRED");
        if (lineage.CandidateSignalUid != signal.SignalUid)
            failures.Add("SIGNAL_LINEAGE_MISMATCH");
        if (!string.Equals(signal.FusionPolicyVersion, lineage.FusionPolicyVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lineage.FusionEngineVersion) ||
            string.IsNullOrWhiteSpace(lineage.WeightConfigurationVersion))
            failures.Add("FUSION_CONFIGURATION_LINEAGE_INVALID");
        if (!SignalContractV1.Directions.Contains(signal.Direction))
            failures.Add("SIGNAL_DIRECTION_UNSUPPORTED");
        if (!SignalContractV1.Timeframes.Contains(signal.PrimaryTimeframe))
            failures.Add("SIGNAL_TIMEFRAME_UNSUPPORTED");
        if (string.IsNullOrWhiteSpace(signal.InstrumentKey) ||
            string.IsNullOrWhiteSpace(signal.StrategyCode) ||
            string.IsNullOrWhiteSpace(signal.StrategyVersion))
            failures.Add("SIGNAL_IDENTITY_INVALID");
        if (signal.Strength is < 0m or > 1m || signal.Confidence is < 0m or > 1m)
            failures.Add("SIGNAL_SCORE_INVALID");
        if (signal.ReferencePrice <= 0m || signal.InvalidationPrice <= 0m)
            failures.Add("SIGNAL_PRICE_INVALID");
        if (signal.GeneratedAtUtc > intake.AsOfUtc)
            failures.Add("SIGNAL_FUTURE_DATED");
        if (signal.ValidUntilUtc <= intake.AsOfUtc || signal.EntryClosesAtUtc <= intake.AsOfUtc)
            failures.Add("SIGNAL_EXPIRED");
        if (signal.EntryOpensAtUtc < signal.GeneratedAtUtc ||
            signal.EntryClosesAtUtc <= signal.EntryOpensAtUtc ||
            signal.ValidUntilUtc < signal.EntryClosesAtUtc)
            failures.Add("SIGNAL_VALIDITY_WINDOW_INVALID");
        if (signal.ExpectedHoldingPeriodMinutes < 1)
            failures.Add("SIGNAL_HOLDING_PERIOD_INVALID");
        if (string.IsNullOrWhiteSpace(intake.RiskPolicyVersion))
            failures.Add("RISK_POLICY_VERSION_REQUIRED");
        if (intake.Portfolio.ObservedAtUtc > intake.AsOfUtc || intake.Operations.ObservedAtUtc > intake.AsOfUtc)
            failures.Add("RISK_SNAPSHOT_FUTURE_DATED");

        return failures.Distinct(StringComparer.Ordinal).ToList();
    }
}
