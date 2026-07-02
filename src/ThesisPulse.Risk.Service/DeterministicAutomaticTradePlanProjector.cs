using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public interface IAutomaticTradePlanProjector
{
    AutomaticTradePlanProjectionV1 Project(AutomaticTradePlanIntakeV1 intake);
}

public sealed class DeterministicAutomaticTradePlanProjector : IAutomaticTradePlanProjector
{
    public AutomaticTradePlanProjectionV1 Project(AutomaticTradePlanIntakeV1 intake)
    {
        var reasons = Validate(intake);
        if (reasons.Count > 0)
        {
            return new AutomaticTradePlanProjectionV1(
                intake.MessageUid,
                intake.CorrelationId,
                AutomaticTradePlanContractV1.Rejected,
                reasons,
                null,
                intake.ReceivedAtUtc);
        }

        var decision = intake.RiskDecision;
        var signal = intake.Signal;
        var execution = intake.Execution;
        var referencePrice = signal.ReferencePrice;
        var minimumPrice = signal.MinimumPrice ?? referencePrice;
        var maximumPrice = signal.MaximumPrice ?? referencePrice;
        var riskPerUnit = decision.Direction == EvidenceDirectionV1.Long
            ? referencePrice - signal.InvalidationPrice
            : signal.InvalidationPrice - referencePrice;
        var targets = execution.Targets
            .OrderBy(target => target.Sequence)
            .Select(target => new TradeTargetProposalV1(
                target.Sequence,
                decision.Direction == EvidenceDirectionV1.Long
                    ? referencePrice + riskPerUnit * target.RiskRewardMultiple
                    : referencePrice - riskPerUnit * target.RiskRewardMultiple,
                target.QuantityFraction))
            .ToArray();
        var requestUid = DeterministicGuid($"trade-plan-request|{decision.RiskDecisionUid:D}|{execution.ExecutionPolicyVersion}");
        var commandUid = DeterministicGuid($"trade-plan-command|{intake.MessageUid:D}|{requestUid:D}");
        var request = new TradePlanBuildRequestV1(
            requestUid,
            intake.CorrelationId,
            decision,
            execution.PositionIntent,
            new TradeEntryProposalV1(
                execution.EntryOrderType,
                referencePrice,
                null,
                null,
                minimumPrice,
                maximumPrice),
            signal.InvalidationPrice,
            execution.StopOrderType,
            execution.StopLimitPrice,
            targets,
            intake.Instrument.LotSize,
            intake.Instrument.RequestedQuantity,
            intake.Instrument.MinimumExecutionQuantity,
            intake.Instrument.AllowPartialFill,
            execution.MaximumSlippageFraction,
            execution.TimeInForce,
            execution.Session,
            execution.ExitPolicy,
            execution.ExecutionPolicyVersion,
            intake.ReceivedAtUtc);

        return new AutomaticTradePlanProjectionV1(
            intake.MessageUid,
            intake.CorrelationId,
            AutomaticTradePlanContractV1.Eligible,
            Array.Empty<string>(),
            new AutomaticTradePlanCommandV1(
                commandUid,
                requestUid,
                intake.MessageUid,
                intake.CausationMessageUid,
                decision.RiskDecisionUid,
                signal.SignalUid,
                decision.ThesisUid,
                intake.CorrelationId,
                request,
                intake.ReceivedAtUtc),
            intake.ReceivedAtUtc);
    }

    private static IReadOnlyCollection<string> Validate(AutomaticTradePlanIntakeV1 intake)
    {
        var reasons = new List<string>();
        var decision = intake.RiskDecision;
        var signal = intake.Signal;
        if (intake.MessageUid == Guid.Empty) reasons.Add("SOURCE_MESSAGE_UID_REQUIRED");
        if (string.IsNullOrWhiteSpace(intake.CorrelationId)) reasons.Add("CORRELATION_ID_REQUIRED");
        if (!string.Equals(decision.Decision, RiskDecisionContractV1.Approved, StringComparison.Ordinal) || decision.Reasons.Count > 0)
            reasons.Add("RISK_DECISION_NOT_APPROVED");
        if (decision.Budget is null || decision.Budget.ExpiresAtUtc <= intake.ReceivedAtUtc)
            reasons.Add("RISK_BUDGET_EXPIRED_OR_MISSING");
        if (decision.SignalUid == Guid.Empty || decision.SignalUid != signal.SignalUid)
            reasons.Add("RISK_SIGNAL_LINEAGE_MISMATCH");
        if (!string.Equals(decision.InstrumentKey, signal.InstrumentKey, StringComparison.Ordinal))
            reasons.Add("RISK_SIGNAL_INSTRUMENT_MISMATCH");
        var expectedDirection = signal.Direction.ToUpperInvariant() switch
        {
            "LONG" => EvidenceDirectionV1.Long,
            "SHORT" => EvidenceDirectionV1.Short,
            _ => EvidenceDirectionV1.Neutral,
        };
        if (decision.Direction != expectedDirection || expectedDirection == EvidenceDirectionV1.Neutral)
            reasons.Add("RISK_SIGNAL_DIRECTION_MISMATCH");
        if (signal.ValidUntilUtc <= intake.ReceivedAtUtc || signal.EntryClosesAtUtc <= intake.ReceivedAtUtc)
            reasons.Add("SIGNAL_EXPIRED");
        if (signal.ReferencePrice <= 0 || signal.InvalidationPrice <= 0 || signal.ReferencePrice == signal.InvalidationPrice)
            reasons.Add("SIGNAL_PRICE_GEOMETRY_INVALID");
        if (intake.Instrument.LotSize <= 0) reasons.Add("LOT_SIZE_INVALID");
        if (intake.Execution.Targets.Count == 0 || intake.Execution.Targets.Any(target => target.Sequence < 1 || target.RiskRewardMultiple <= 0 || target.QuantityFraction <= 0))
            reasons.Add("TARGET_POLICY_INVALID");
        if (Math.Abs(intake.Execution.Targets.Sum(target => target.QuantityFraction) - 1m) > 0.000001m)
            reasons.Add("TARGET_FRACTIONS_INVALID");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
