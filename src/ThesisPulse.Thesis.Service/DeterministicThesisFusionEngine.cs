using Microsoft.Extensions.Options;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Thesis.Service;

public interface IThesisFusionEngine
{
    ThesisFusionResultV1 Evaluate(ThesisFusionRequestV1 request);
}

public sealed class DeterministicThesisFusionEngine(IOptions<DeterministicFusionOptions> options) : IThesisFusionEngine
{
    private readonly DeterministicFusionOptions _policy = options.Value;

    public ThesisFusionResultV1 Evaluate(ThesisFusionRequestV1 request)
    {
        var now = request.AsOfUtc;
        var failures = Validate(request, now);
        var evidence = BuildEvidence(request, now);
        var (longScore, shortScore) = Score(evidence);
        var direction = longScore >= shortScore ? EvidenceDirectionV1.Long : EvidenceDirectionV1.Short;
        var winningScore = Math.Max(longScore, shortScore);
        var separation = Math.Abs(longScore - shortScore);
        var confidence = CalculateConfidence(request, direction, winningScore);

        if (failures.Count == 0)
        {
            if (winningScore < _policy.MinimumCandidateScore)
                failures.Add("WINNING_SCORE_BELOW_THRESHOLD");
            if (separation < _policy.MinimumScoreSeparation)
                failures.Add("DIRECTIONAL_CONFLICT");
            if (confidence < _policy.MinimumCandidateConfidence)
                failures.Add("CONFIDENCE_BELOW_THRESHOLD");
        }

        var thesisUid = Guid.NewGuid();
        var candidate = failures.Count == 0
            ? new CanonicalCandidateSignalV1(
                Guid.NewGuid(), ThesisFusionContractV1.CandidateStatus, request.InstrumentKey, direction,
                request.PrimaryTimeframe, winningScore, confidence, now,
                request.WeightConfigurationVersion, thesisUid)
            : null;

        return new ThesisFusionResultV1(
            thesisUid, request.RequestUid, request.CorrelationId, request.InstrumentKey,
            candidate is null ? "REJECTED_BY_FUSION" : ThesisFusionContractV1.CandidateStatus,
            candidate is null ? EvidenceDirectionV1.Neutral : direction,
            longScore, shortScore, confidence,
            BuildSummary(request, direction, longScore, shortScore, failures),
            failures, evidence, candidate, _policy.EngineVersion,
            request.WeightConfigurationVersion, now);
    }

    private List<string> Validate(ThesisFusionRequestV1 request, DateTimeOffset now)
    {
        var failures = new List<string>();
        if (!string.Equals(request.WeightConfigurationVersion, _policy.WeightConfigurationVersion, StringComparison.Ordinal))
            failures.Add("UNKNOWN_WEIGHT_CONFIGURATION_VERSION");
        if (request.DirectionalEvidence.Count < _policy.MinimumDirectionalEngines)
            failures.Add("INSUFFICIENT_DIRECTIONAL_ENGINES");
        if (request.DirectionalEvidence.Any(x => x.Score is < 0 or > 100 || x.Confidence is < 0 or > 100))
            failures.Add("INVALID_DIRECTIONAL_SCORE");
        if (request.TimeframeConfirmations.Any(x => x.Score is < 0 or > 100 || x.Confidence is < 0 or > 100))
            failures.Add("INVALID_TIMEFRAME_SCORE");
        if (request.DirectionalEvidence.Any(x => now - x.ObservedAtUtc > TimeSpan.FromSeconds(_policy.MaximumInputAgeSeconds)) ||
            now - request.Regime.ObservedAtUtc > TimeSpan.FromSeconds(_policy.MaximumInputAgeSeconds))
            failures.Add("STALE_REQUIRED_INPUT");

        var primary = request.TimeframeConfirmations.FirstOrDefault(x =>
            string.Equals(x.Timeframe, request.PrimaryTimeframe, StringComparison.OrdinalIgnoreCase));
        if (primary is null) failures.Add("PRIMARY_TIMEFRAME_MISSING");
        else
        {
            if (!primary.IsClosedCandle) failures.Add("PRIMARY_CANDLE_NOT_CLOSED");
            if (primary.Direction == EvidenceDirectionV1.Neutral) failures.Add("PRIMARY_DIRECTION_NEUTRAL");
            if (primary.Confidence < _policy.MinimumPrimaryTimeframeConfidence) failures.Add("PRIMARY_CONFIDENCE_BELOW_THRESHOLD");
            var confirming = request.TimeframeConfirmations.Count(x => x.Direction == primary.Direction);
            if (confirming < _policy.MinimumConfirmingTimeframes) failures.Add("INSUFFICIENT_TIMEFRAME_CONFIRMATION");
            if (request.TimeframeConfirmations.Any(x => IsHigherTimeframe(x.Timeframe) && x.Direction == Opposite(primary.Direction) && x.Confidence >= _policy.HigherTimeframeVetoConfidence))
                failures.Add("OPPOSING_HIGHER_TIMEFRAME_VETO");
            if (request.Regime.DirectionalBias != EvidenceDirectionV1.Neutral && request.Regime.DirectionalBias == Opposite(primary.Direction) && request.Regime.Confidence >= _policy.HigherTimeframeVetoConfidence)
                failures.Add("REGIME_DIRECTION_VETO");
        }
        return failures.Distinct(StringComparer.Ordinal).ToList();
    }

    private List<ThesisEvidenceV1> BuildEvidence(ThesisFusionRequestV1 request, DateTimeOffset now) =>
        request.DirectionalEvidence
            .Where(x => now - x.ObservedAtUtc <= TimeSpan.FromSeconds(_policy.MaximumInputAgeSeconds))
            .Where(x => _policy.EngineWeights.ContainsKey(x.EngineCode) && _policy.TimeframeWeights.ContainsKey(x.Timeframe))
            .Select(x =>
            {
                var weight = _policy.EngineWeights[x.EngineCode] * _policy.TimeframeWeights[x.Timeframe];
                var contribution = x.Score / 100m * x.Confidence / 100m * weight;
                return new ThesisEvidenceV1(x.EngineCode, x.Timeframe, x.Direction, x.Score, x.Confidence, weight, contribution, x.Reasons);
            }).ToList();

    private static (decimal Long, decimal Short) Score(IReadOnlyCollection<ThesisEvidenceV1> evidence)
    {
        decimal Normalize(EvidenceDirectionV1 direction)
        {
            var side = evidence.Where(x => x.Direction == direction).ToList();
            var denominator = side.Sum(x => x.AppliedWeight);
            return denominator == 0 ? 0 : Math.Round(side.Sum(x => x.Contribution) / denominator * 100m, 2);
        }
        return (Normalize(EvidenceDirectionV1.Long), Normalize(EvidenceDirectionV1.Short));
    }

    private decimal CalculateConfidence(ThesisFusionRequestV1 request, EvidenceDirectionV1 direction, decimal winningScore)
    {
        var eligible = request.DirectionalEvidence.Where(x => _policy.EngineWeights.ContainsKey(x.EngineCode)).ToList();
        var totalEngineWeight = eligible.Sum(x => _policy.EngineWeights[x.EngineCode]);
        var engineAgreement = totalEngineWeight == 0 ? 0 : eligible.Where(x => x.Direction == direction).Sum(x => _policy.EngineWeights[x.EngineCode]) / totalEngineWeight * 100m;
        var tf = request.TimeframeConfirmations.Where(x => _policy.TimeframeWeights.ContainsKey(x.Timeframe)).ToList();
        var totalTfWeight = tf.Sum(x => _policy.TimeframeWeights[x.Timeframe]);
        var timeframeAgreement = totalTfWeight == 0 ? 0 : tf.Where(x => x.Direction == direction).Sum(x => _policy.TimeframeWeights[x.Timeframe]) / totalTfWeight * 100m;
        return Math.Round(winningScore * .40m + engineAgreement * .25m + timeframeAgreement * .20m + request.Regime.Confidence * .15m, 2);
    }

    private static string BuildSummary(ThesisFusionRequestV1 request, EvidenceDirectionV1 direction, decimal longScore, decimal shortScore, IReadOnlyCollection<string> failures) =>
        failures.Count == 0
            ? $"{direction.ToString().ToUpperInvariant()} thesis for {request.InstrumentKey}: long {longScore:F2}, short {shortScore:F2}; all deterministic gates passed."
            : $"No candidate for {request.InstrumentKey}: {string.Join(", ", failures)}. Long {longScore:F2}, short {shortScore:F2}.";

    private static bool IsHigherTimeframe(string timeframe) => timeframe is "1h" or "1d";
    private static EvidenceDirectionV1 Opposite(EvidenceDirectionV1 direction) => direction == EvidenceDirectionV1.Long ? EvidenceDirectionV1.Short : EvidenceDirectionV1.Long;
}
