using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public static class OptionChainAdvancedAnalytics
{
    public static IReadOnlyCollection<OptionChainStrikeActivityV1> CalculateActivities(
        IReadOnlyCollection<OptionChainExpiryInputV1> expiries)
    {
        return expiries
            .OrderBy(expiry => expiry.ExpiryDate)
            .SelectMany(expiry => expiry.Strikes
                .OrderBy(strike => strike.StrikePrice)
                .SelectMany(strike => new[]
                {
                    Classify(
                        expiry.ExpiryDate,
                        strike.StrikePrice,
                        "CALL",
                        strike.CallOpenInterestChange,
                        strike.CallPriceChange,
                        strike.CallVolume),
                    Classify(
                        expiry.ExpiryDate,
                        strike.StrikePrice,
                        "PUT",
                        strike.PutOpenInterestChange,
                        strike.PutPriceChange,
                        strike.PutVolume),
                }))
            .ToArray();
    }

    public static IReadOnlyCollection<OptionChainIvStructureV1> CalculateIvStructure(
        IReadOnlyCollection<OptionChainExpiryInputV1> expiries)
    {
        var ordered = expiries.OrderBy(expiry => expiry.ExpiryDate).ToArray();
        var results = new List<OptionChainIvStructureV1>(ordered.Length);

        for (var index = 0; index < ordered.Length; index++)
        {
            var expiry = ordered[index];
            var atm = expiry.Strikes
                .OrderBy(strike => Math.Abs(strike.StrikePrice - expiry.UnderlyingPrice))
                .ThenBy(strike => strike.StrikePrice)
                .First();

            var lowerWing = expiry.Strikes
                .Where(strike => strike.StrikePrice < expiry.UnderlyingPrice)
                .OrderBy(strike => strike.StrikePrice)
                .FirstOrDefault();
            var upperWing = expiry.Strikes
                .Where(strike => strike.StrikePrice > expiry.UnderlyingPrice)
                .OrderByDescending(strike => strike.StrikePrice)
                .FirstOrDefault();

            var putCallSkew = Difference(atm.PutImpliedVolatility, atm.CallImpliedVolatility);
            var lowerPutIv = lowerWing?.PutImpliedVolatility;
            var upperCallIv = upperWing?.CallImpliedVolatility;
            var wingSkew = Difference(lowerPutIv, upperCallIv);

            decimal? termPremium = null;
            if (index + 1 < ordered.Length)
            {
                var nextExpiry = ordered[index + 1];
                var nextAtm = nextExpiry.Strikes
                    .OrderBy(strike => Math.Abs(strike.StrikePrice - nextExpiry.UnderlyingPrice))
                    .ThenBy(strike => strike.StrikePrice)
                    .First();
                termPremium = Difference(
                    Average(nextAtm.CallImpliedVolatility, nextAtm.PutImpliedVolatility),
                    Average(atm.CallImpliedVolatility, atm.PutImpliedVolatility));
            }

            results.Add(new OptionChainIvStructureV1(
                expiry.ExpiryDate,
                Round(atm.CallImpliedVolatility),
                Round(atm.PutImpliedVolatility),
                Round(putCallSkew),
                Round(wingSkew),
                Round(termPremium)));
        }

        return results;
    }

    private static OptionChainStrikeActivityV1 Classify(
        DateOnly expiryDate,
        decimal strikePrice,
        string optionSide,
        decimal oiChange,
        decimal? priceChange,
        decimal volume)
    {
        var classification = OptionChainContractConstantsV1.Unchanged;
        var reasons = new List<string>
        {
            $"OI_CHANGE={oiChange}",
            $"PRICE_CHANGE={(priceChange.HasValue ? priceChange.Value : 0m)}",
            $"VOLUME={volume}",
        };

        if (priceChange.HasValue && volume > 0)
        {
            classification = (priceChange.Value, oiChange) switch
            {
                (> 0m, > 0m) => OptionChainContractConstantsV1.LongBuildup,
                (< 0m, > 0m) => OptionChainContractConstantsV1.ShortBuildup,
                (> 0m, < 0m) => OptionChainContractConstantsV1.ShortCovering,
                (< 0m, < 0m) => OptionChainContractConstantsV1.LongUnwinding,
                _ => OptionChainContractConstantsV1.Unchanged,
            };
        }
        else if (!priceChange.HasValue)
        {
            reasons.Add("PRICE_CHANGE_UNAVAILABLE");
        }

        return new OptionChainStrikeActivityV1(
            expiryDate,
            strikePrice,
            optionSide,
            classification,
            oiChange,
            volume,
            reasons);
    }

    private static decimal? Average(decimal? first, decimal? second)
    {
        if (!first.HasValue || !second.HasValue)
            return null;
        return (first.Value + second.Value) / 2m;
    }

    private static decimal? Difference(decimal? first, decimal? second)
    {
        if (!first.HasValue || !second.HasValue)
            return null;
        return first.Value - second.Value;
    }

    private static decimal? Round(decimal? value) =>
        value.HasValue
            ? decimal.Round(value.Value, 6, MidpointRounding.AwayFromZero)
            : null;
}
