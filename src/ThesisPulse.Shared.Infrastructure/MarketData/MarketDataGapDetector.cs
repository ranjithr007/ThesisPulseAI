using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record MarketDataRecoveryOptions
{
    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 60;
    public int GracePeriodSeconds { get; init; } = 45;
    public int MaximumGapCandles { get; init; } = 500;
    public int MaximumRecoveryDays { get; init; } = 30;
    public TimeOnly SessionOpenIndia { get; init; } = new(9, 15);
    public TimeOnly SessionCloseIndia { get; init; } = new(15, 30);

    public void Validate()
    {
        if (PollIntervalSeconds is < 15 or > 3600 ||
            GracePeriodSeconds is < 0 or > 3600 ||
            MaximumGapCandles is < 1 or > 10000 ||
            MaximumRecoveryDays is < 1 or > 3650 ||
            SessionCloseIndia <= SessionOpenIndia)
        {
            throw new InvalidOperationException(
                "One or more Market Data recovery settings are invalid.");
        }
    }
}

public sealed class MarketDataGapDetector(
    MarketDataRecoveryOptions options) : IMarketDataGapDetector
{
    private static readonly TimeZoneInfo IndiaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public MarketDataGap? Detect(
        MarketDataSubscriptionItem subscription,
        IReadOnlyCollection<StoredCandleV1> latestCandles,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(latestCandles);

        var duration = MarketDataContractV1.GetDuration(
            subscription.RecoveryTimeframe);
        var expectedCloseUtc = GetExpectedLatestCloseUtc(
            evaluatedAtUtc,
            duration);

        if (!expectedCloseUtc.HasValue)
        {
            return null;
        }

        var latest = latestCandles
            .Where(candle => candle.Timeframe.Equals(
                subscription.RecoveryTimeframe,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candle => candle.CloseAtUtc)
            .FirstOrDefault();

        if (latest is null)
        {
            var gapStart = GetSessionOpenUtc(expectedCloseUtc.Value);
            var count = CalculateExpectedCount(
                gapStart,
                expectedCloseUtc.Value,
                duration);
            return count > 0
                ? new MarketDataGap(
                    subscription.ProviderInstrumentKey,
                    subscription.RecoveryTimeframe,
                    gapStart,
                    expectedCloseUtc.Value,
                    Math.Min(count, options.MaximumGapCandles),
                    "NO_CANDLE_HISTORY")
                : null;
        }

        var nextExpectedClose = latest.CloseAtUtc.Add(duration);
        if (nextExpectedClose > expectedCloseUtc.Value)
        {
            return null;
        }

        var expectedCount = CalculateExpectedCount(
            nextExpectedClose,
            expectedCloseUtc.Value,
            duration);
        if (expectedCount <= 0)
        {
            return null;
        }

        return new MarketDataGap(
            subscription.ProviderInstrumentKey,
            subscription.RecoveryTimeframe,
            nextExpectedClose.Subtract(duration),
            expectedCloseUtc.Value,
            Math.Min(expectedCount, options.MaximumGapCandles),
            "MISSING_CLOSED_CANDLES");
    }

    private DateTimeOffset? GetExpectedLatestCloseUtc(
        DateTimeOffset evaluatedAtUtc,
        TimeSpan duration)
    {
        var IndiaNow = TimeZoneInfo.ConvertTime(evaluatedAtUtc, IndiaTimeZone);
        if (IndiaNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return null;
        }

        var localDate = DateOnly.FromDateTime(IndiaNow.DateTime);
        var openLocal = localDate.ToDateTime(
            options.SessionOpenIndia,
            DateTimeKind.Unspecified);
        var closeLocal = localDate.ToDateTime(
            options.SessionCloseIndia,
            DateTimeKind.Unspecified);
        var effectiveLocal = IndiaNow.DateTime.AddSeconds(-options.GracePeriodSeconds);

        if (effectiveLocal < openLocal.Add(duration))
        {
            return null;
        }

        if (effectiveLocal > closeLocal)
        {
            effectiveLocal = closeLocal;
        }

        var elapsed = effectiveLocal - openLocal;
        var closedIntervals = (long)Math.Floor(
            elapsed.TotalMilliseconds / duration.TotalMilliseconds);
        if (closedIntervals < 1)
        {
            return null;
        }

        var expectedCloseLocal = openLocal.AddTicks(duration.Ticks * closedIntervals);
        return ConvertIndiaLocalToUtc(expectedCloseLocal);
    }

    private DateTimeOffset GetSessionOpenUtc(DateTimeOffset referenceUtc)
    {
        var india = TimeZoneInfo.ConvertTime(referenceUtc, IndiaTimeZone);
        var local = DateOnly.FromDateTime(india.DateTime).ToDateTime(
            options.SessionOpenIndia,
            DateTimeKind.Unspecified);
        return ConvertIndiaLocalToUtc(local);
    }

    private static DateTimeOffset ConvertIndiaLocalToUtc(DateTime local)
    {
        var offset = IndiaTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static int CalculateExpectedCount(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeSpan duration)
    {
        if (endUtc <= startUtc)
        {
            return 0;
        }

        return checked((int)Math.Floor(
            (endUtc - startUtc).TotalMilliseconds /
            duration.TotalMilliseconds));
    }
}
