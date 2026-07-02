using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public static class OptionChainIntelligenceEvaluator
{
    public static OptionChainIntelligenceEvidenceV1 Evaluate(OptionChainEvaluationInputV1 input)
    {
        var rejectionReasons = Validate(input);
        if (rejectionReasons.Count > 0)
            return Rejected(input, rejectionReasons);

        var expiries = input.Expiries
            .OrderBy(expiry => expiry.ExpiryDate)
            .ToArray();
        var allStrikes = expiries.SelectMany(expiry => expiry.Strikes).ToArray();

        var totalCallOi = allStrikes.Sum(strike => strike.CallOpenInterest);
        var totalPutOi = allStrikes.Sum(strike => strike.PutOpenInterest);
        var totalCallVolume = allStrikes.Sum(strike => strike.CallVolume);
        var totalPutVolume = allStrikes.Sum(strike => strike.PutVolume);

        var pcrOi = Ratio(totalPutOi, totalCallOi);
        var pcrVolume = Ratio(totalPutVolume, totalCallVolume);
        var nearestExpiry = expiries[0];
        var maxPainStrike = CalculateMaxPain(nearestExpiry);
        var walls = CalculateWalls(expiries);
        var activities = OptionChainAdvancedAnalytics.CalculateActivities(expiries);
        var ivStructure = OptionChainAdvancedAnalytics.CalculateIvStructure(expiries);

        var bullishScore = Clamp01(((pcrOi - 1m) * 0.5m) + ((pcrVolume - 1m) * 0.5m));
        var bearishScore = Clamp01(((1m - pcrOi) * 0.5m) + ((1m - pcrVolume) * 0.5m));
        var directionalBias = bullishScore > bearishScore + 0.10m
            ? OptionChainContractConstantsV1.Bullish
            : bearishScore > bullishScore + 0.10m
                ? OptionChainContractConstantsV1.Bearish
                : OptionChainContractConstantsV1.Neutral;

        var confidence = Clamp01(Math.Abs(bullishScore - bearishScore) + 0.40m);
        var reasons = BuildReasons(
            pcrOi,
            pcrVolume,
            maxPainStrike,
            nearestExpiry.UnderlyingPrice,
            directionalBias,
            ivStructure);
        var evidenceUid = DeterministicGuid(
            input.SourceSnapshotUid,
            input.EngineVersion,
            input.InstrumentCode,
            input.SourceAsOfUtc);

        return new OptionChainIntelligenceEvidenceV1(
            evidenceUid,
            input.RequestUid,
            input.SourceSnapshotUid,
            OptionChainContractConstantsV1.ContractVersion,
            input.EngineVersion,
            input.InstrumentCode,
            input.Environment,
            input.SourceAsOfUtc,
            input.EvaluatedAtUtc,
            pcrOi,
            pcrVolume,
            maxPainStrike,
            directionalBias,
            bullishScore,
            bearishScore,
            confidence,
            walls,
            activities,
            ivStructure,
            reasons,
            Array.Empty<string>());
    }

    private static IReadOnlyCollection<string> Validate(OptionChainEvaluationInputV1 input)
    {
        var reasons = new List<string>();
        if (input.RequestUid == Guid.Empty) reasons.Add("REQUEST_UID_REQUIRED");
        if (input.SourceSnapshotUid == Guid.Empty) reasons.Add("SOURCE_SNAPSHOT_UID_REQUIRED");
        if (string.IsNullOrWhiteSpace(input.InstrumentCode)) reasons.Add("INSTRUMENT_CODE_REQUIRED");
        if (!string.Equals(input.Environment, "PAPER", StringComparison.Ordinal)) reasons.Add("PAPER_ENVIRONMENT_REQUIRED");
        if (string.IsNullOrWhiteSpace(input.EngineVersion)) reasons.Add("ENGINE_VERSION_REQUIRED");
        if (input.EvaluatedAtUtc < input.SourceAsOfUtc) reasons.Add("EVALUATED_BEFORE_SOURCE");
        if (input.Expiries is null || input.Expiries.Count == 0) reasons.Add("EXPIRY_REQUIRED");

        if (input.Expiries is not null)
        {
            foreach (var expiry in input.Expiries)
            {
                if (expiry.UnderlyingPrice <= 0) reasons.Add("UNDERLYING_PRICE_INVALID");
                if (expiry.Strikes is null || expiry.Strikes.Count < 3) reasons.Add("INSUFFICIENT_STRIKES");
                if (expiry.Strikes is null) continue;

                var duplicateStrike = expiry.Strikes
                    .GroupBy(strike => strike.StrikePrice)
                    .Any(group => group.Count() > 1);
                if (duplicateStrike) reasons.Add("DUPLICATE_STRIKE");

                if (expiry.Strikes.Any(strike =>
                    strike.StrikePrice <= 0 ||
                    strike.CallOpenInterest < 0 ||
                    strike.PutOpenInterest < 0 ||
                    strike.CallVolume < 0 ||
                    strike.PutVolume < 0 ||
                    strike.CallImpliedVolatility < 0 ||
                    strike.PutImpliedVolatility < 0))
                {
                    reasons.Add("NEGATIVE_OR_INVALID_CHAIN_VALUE");
                }
            }
        }

        return reasons.Distinct(StringComparer.Ordinal).OrderBy(reason => reason).ToArray();
    }

    private static decimal Ratio(decimal numerator, decimal denominator) =>
        denominator <= 0 ? 0m : decimal.Round(numerator / denominator, 6, MidpointRounding.AwayFromZero);

    private static decimal CalculateMaxPain(OptionChainExpiryInputV1 expiry)
    {
        return expiry.Strikes
            .OrderBy(candidate => candidate.StrikePrice)
            .Select(candidate => new
            {
                candidate.StrikePrice,
                Pain = expiry.Strikes.Sum(strike =>
                    Math.Max(0m, candidate.StrikePrice - strike.StrikePrice) * strike.CallOpenInterest +
                    Math.Max(0m, strike.StrikePrice - candidate.StrikePrice) * strike.PutOpenInterest),
            })
            .OrderBy(result => result.Pain)
            .ThenBy(result => Math.Abs(result.StrikePrice - expiry.UnderlyingPrice))
            .ThenBy(result => result.StrikePrice)
            .First()
            .StrikePrice;
    }

    private static IReadOnlyCollection<OptionChainOiWallV1> CalculateWalls(
        IReadOnlyCollection<OptionChainExpiryInputV1> expiries)
    {
        var walls = new List<OptionChainOiWallV1>();
        foreach (var expiry in expiries.OrderBy(value => value.ExpiryDate))
        {
            var callWall = expiry.Strikes
                .OrderByDescending(strike => strike.CallOpenInterest)
                .ThenBy(strike => Math.Abs(strike.StrikePrice - expiry.UnderlyingPrice))
                .ThenBy(strike => strike.StrikePrice)
                .First();
            var putWall = expiry.Strikes
                .OrderByDescending(strike => strike.PutOpenInterest)
                .ThenBy(strike => Math.Abs(strike.StrikePrice - expiry.UnderlyingPrice))
                .ThenBy(strike => strike.StrikePrice)
                .First();

            walls.Add(new OptionChainOiWallV1(
                expiry.ExpiryDate,
                callWall.StrikePrice,
                "CALL",
                callWall.CallOpenInterest,
                DistanceFraction(callWall.StrikePrice, expiry.UnderlyingPrice)));
            walls.Add(new OptionChainOiWallV1(
                expiry.ExpiryDate,
                putWall.StrikePrice,
                "PUT",
                putWall.PutOpenInterest,
                DistanceFraction(putWall.StrikePrice, expiry.UnderlyingPrice)));
        }

        return walls;
    }

    private static IReadOnlyCollection<string> BuildReasons(
        decimal pcrOi,
        decimal pcrVolume,
        decimal maxPain,
        decimal underlying,
        string bias,
        IReadOnlyCollection<OptionChainIvStructureV1> ivStructure)
    {
        var reasons = new List<string>
        {
            $"PCR_OI={pcrOi}",
            $"PCR_VOLUME={pcrVolume}",
            $"MAX_PAIN={maxPain}",
            $"MAX_PAIN_DISTANCE={DistanceFraction(maxPain, underlying)}",
            $"BIAS={bias}",
        };

        var nearestIv = ivStructure.OrderBy(value => value.ExpiryDate).FirstOrDefault();
        if (nearestIv is not null)
        {
            reasons.Add($"ATM_PUT_CALL_SKEW={nearestIv.PutCallSkew}");
            reasons.Add($"WING_SKEW={nearestIv.WingSkew}");
            reasons.Add($"TERM_PREMIUM={nearestIv.TermPremiumToNextExpiry}");
        }

        return reasons;
    }

    private static decimal DistanceFraction(decimal strike, decimal underlying) =>
        underlying <= 0 ? 0m : decimal.Round(Math.Abs(strike - underlying) / underlying, 8, MidpointRounding.AwayFromZero);

    private static decimal Clamp01(decimal value) => Math.Max(0m, Math.Min(1m, value));

    private static Guid DeterministicGuid(Guid sourceSnapshotUid, string engineVersion, string instrumentCode, DateTimeOffset asOfUtc)
    {
        var material = $"{sourceSnapshotUid:N}|{engineVersion}|{instrumentCode}|{asOfUtc:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static OptionChainIntelligenceEvidenceV1 Rejected(
        OptionChainEvaluationInputV1 input,
        IReadOnlyCollection<string> rejectionReasons) => new(
            DeterministicGuid(input.SourceSnapshotUid, input.EngineVersion ?? string.Empty, input.InstrumentCode ?? string.Empty, input.SourceAsOfUtc),
            input.RequestUid,
            input.SourceSnapshotUid,
            OptionChainContractConstantsV1.ContractVersion,
            input.EngineVersion ?? string.Empty,
            input.InstrumentCode ?? string.Empty,
            input.Environment ?? string.Empty,
            input.SourceAsOfUtc,
            input.EvaluatedAtUtc,
            0m,
            0m,
            0m,
            OptionChainContractConstantsV1.Neutral,
            0m,
            0m,
            0m,
            Array.Empty<OptionChainOiWallV1>(),
            Array.Empty<OptionChainStrikeActivityV1>(),
            Array.Empty<OptionChainIvStructureV1>(),
            Array.Empty<string>(),
            rejectionReasons);
}
