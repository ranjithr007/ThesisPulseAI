using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record MarketDataFreshnessOptions
{
    public string PolicyVersion { get; init; } = "market-freshness-v1.0.0";

    public TimeSpan LiveQuoteMaximumAge { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan OneMinuteMaximumAge { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan FiveMinuteMaximumAge { get; init; } = TimeSpan.FromMinutes(7);

    public TimeSpan FifteenMinuteMaximumAge { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan OneHourMaximumAge { get; init; } = TimeSpan.FromMinutes(75);

    public TimeSpan OneDayMaximumAge { get; init; } = TimeSpan.FromHours(36);

    public decimal ExitAgeMultiplier { get; init; } = 5m;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PolicyVersion);

        var ages = new[]
        {
            LiveQuoteMaximumAge,
            OneMinuteMaximumAge,
            FiveMinuteMaximumAge,
            FifteenMinuteMaximumAge,
            OneHourMaximumAge,
            OneDayMaximumAge,
        };

        if (ages.Any(age => age <= TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                "All market-data freshness ages must be greater than zero.");
        }

        if (ExitAgeMultiplier < 1m)
        {
            throw new InvalidOperationException(
                "Market-data exit age multiplier must be at least one.");
        }
    }
}

public sealed class MarketDataFreshnessEvaluator(
    MarketDataFreshnessOptions options) : IMarketDataFreshnessEvaluator
{
    public MarketDataFreshnessAssessmentV1 EvaluateCandle(
        CanonicalCandleV1 candle,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candle);
        var maximumAge = GetCandleMaximumAge(candle.Timeframe);
        var basis = candle.CloseAtUtc;
        var reasons = ValidateCandle(candle);

        return Evaluate(
            basis,
            evaluatedAtUtc,
            maximumAge,
            reasons,
            candle.IsClosed);
    }

    public MarketDataFreshnessAssessmentV1 EvaluateLiveUpdate(
        CanonicalLiveMarketUpdateV1 update,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(update);
        var reasons = new List<string>();

        if (update.LastTradedPrice is <= 0)
        {
            reasons.Add("LAST_TRADED_PRICE_MISSING_OR_INVALID");
        }

        if (update.EventAtUtc == default)
        {
            reasons.Add("EVENT_TIMESTAMP_MISSING");
        }

        return Evaluate(
            update.EventAtUtc,
            evaluatedAtUtc,
            options.LiveQuoteMaximumAge,
            reasons,
            isClosedCandle: true);
    }

    private MarketDataFreshnessAssessmentV1 Evaluate(
        DateTimeOffset freshnessBasisUtc,
        DateTimeOffset evaluatedAtUtc,
        TimeSpan maximumAge,
        IReadOnlyCollection<string> validationReasons,
        bool isClosedCandle)
    {
        var reasons = validationReasons.ToList();
        var rawAge = evaluatedAtUtc - freshnessBasisUtc;

        if (rawAge < TimeSpan.Zero)
        {
            reasons.Add("FUTURE_TIMESTAMP");
        }

        var age = rawAge < TimeSpan.Zero ? TimeSpan.Zero : rawAge;
        var invalid = reasons.Any(reason =>
            reason is "FUTURE_TIMESTAMP"
                or "OHLC_RELATIONSHIP_INVALID"
                or "PRICE_MISSING_OR_INVALID"
                or "VOLUME_NEGATIVE"
                or "EVENT_TIMESTAMP_MISSING");
        var incomplete = !invalid && reasons.Count > 0;
        var stale = !invalid && !incomplete && age > maximumAge;
        var qualityStatus = invalid
            ? MarketDataQualityStatusV1.Invalid
            : incomplete
                ? MarketDataQualityStatusV1.Incomplete
                : stale
                    ? MarketDataQualityStatusV1.Stale
                    : MarketDataQualityStatusV1.Valid;

        if (stale)
        {
            reasons.Add("FRESHNESS_LIMIT_EXCEEDED");
        }

        var exitMaximumAgeMilliseconds =
            (long)(maximumAge.TotalMilliseconds * (double)options.ExitAgeMultiplier);
        var usableForNewExposure =
            qualityStatus == MarketDataQualityStatusV1.Valid && isClosedCandle;
        var usableForExit =
            !invalid && age.TotalMilliseconds <= exitMaximumAgeMilliseconds;

        return new MarketDataFreshnessAssessmentV1(
            qualityStatus,
            evaluatedAtUtc,
            freshnessBasisUtc,
            (long)age.TotalMilliseconds,
            (long)maximumAge.TotalMilliseconds,
            usableForNewExposure,
            usableForExit,
            options.PolicyVersion,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private TimeSpan GetCandleMaximumAge(string timeframe) => timeframe switch
    {
        "1m" => options.OneMinuteMaximumAge,
        "5m" => options.FiveMinuteMaximumAge,
        "15m" => options.FifteenMinuteMaximumAge,
        "1h" => options.OneHourMaximumAge,
        "1d" => options.OneDayMaximumAge,
        _ => throw new ArgumentOutOfRangeException(
            nameof(timeframe),
            timeframe,
            "Unsupported candle timeframe."),
    };

    private static IReadOnlyCollection<string> ValidateCandle(
        CanonicalCandleV1 candle)
    {
        var reasons = new List<string>();

        if (candle.OpenPrice <= 0 ||
            candle.HighPrice <= 0 ||
            candle.LowPrice <= 0 ||
            candle.ClosePrice <= 0)
        {
            reasons.Add("PRICE_MISSING_OR_INVALID");
        }

        if (candle.HighPrice < candle.LowPrice ||
            candle.HighPrice < candle.OpenPrice ||
            candle.HighPrice < candle.ClosePrice ||
            candle.LowPrice > candle.OpenPrice ||
            candle.LowPrice > candle.ClosePrice)
        {
            reasons.Add("OHLC_RELATIONSHIP_INVALID");
        }

        if (candle.VolumeQuantity < 0)
        {
            reasons.Add("VOLUME_NEGATIVE");
        }

        if (!candle.IsClosed)
        {
            reasons.Add("CANDLE_NOT_CLOSED");
        }

        if (!MarketDataContractV1.Timeframes.Contains(candle.Timeframe))
        {
            reasons.Add("TIMEFRAME_UNSUPPORTED");
        }

        return reasons;
    }
}
