using System.Globalization;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxDecodedCandle(
    string Interval,
    string Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record UpstoxDecodedMarketFeed(
    string InstrumentKey,
    string? FeedType,
    long? ExchangeTimestampMilliseconds,
    decimal? LastTradedPrice,
    decimal? LastTradedQuantity,
    long? LastTradeTimestampMilliseconds,
    decimal? PreviousClosePrice,
    decimal? OpenInterest,
    decimal? TotalBuyQuantity,
    decimal? TotalSellQuantity,
    IReadOnlyCollection<UpstoxDecodedCandle>? Candles,
    string? SourceSequence,
    string? RawPayloadJson);

public interface IUpstoxLiveFeedNormalizer
{
    IReadOnlyCollection<CanonicalLiveMarketUpdateV1> Normalize(
        IReadOnlyCollection<UpstoxDecodedMarketFeed> feeds,
        DateTimeOffset receivedAtUtc);
}

public sealed class UpstoxLiveFeedNormalizer : IUpstoxLiveFeedNormalizer
{
    public IReadOnlyCollection<CanonicalLiveMarketUpdateV1> Normalize(
        IReadOnlyCollection<UpstoxDecodedMarketFeed> feeds,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        var updates = new List<CanonicalLiveMarketUpdateV1>(feeds.Count);

        foreach (var feed in feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.InstrumentKey))
            {
                continue;
            }

            var eventAtUtc = ResolveEventTime(feed, receivedAtUtc);
            var publishedAtUtc = feed.ExchangeTimestampMilliseconds.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(
                    feed.ExchangeTimestampMilliseconds.Value)
                : eventAtUtc;
            var candles = NormalizeCandles(feed.Candles);
            var rawPayload = string.IsNullOrWhiteSpace(feed.RawPayloadJson)
                ? JsonSerializer.Serialize(feed)
                : feed.RawPayloadJson;
            var eventId = BuildEventId(feed, eventAtUtc, rawPayload);

            updates.Add(new CanonicalLiveMarketUpdateV1(
                ProviderCode: "UPSTOX",
                ProviderInstrumentKey: feed.InstrumentKey,
                SourceEventId: eventId,
                EventAtUtc: eventAtUtc,
                PublishedAtUtc: publishedAtUtc,
                ReceivedAtUtc: receivedAtUtc,
                LastTradedPrice: feed.LastTradedPrice,
                LastTradedQuantity: feed.LastTradedQuantity,
                PreviousClosePrice: feed.PreviousClosePrice,
                OpenInterest: feed.OpenInterest,
                TotalBuyQuantity: feed.TotalBuyQuantity,
                TotalSellQuantity: feed.TotalSellQuantity,
                CandleSnapshots: candles,
                SourceVersion: "upstox-market-feed-v3",
                RawPayloadJson: rawPayload));
        }

        return updates;
    }

    private static DateTimeOffset ResolveEventTime(
        UpstoxDecodedMarketFeed feed,
        DateTimeOffset receivedAtUtc)
    {
        if (feed.LastTradeTimestampMilliseconds.HasValue &&
            feed.LastTradeTimestampMilliseconds.Value > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                feed.LastTradeTimestampMilliseconds.Value);
        }

        if (feed.ExchangeTimestampMilliseconds.HasValue &&
            feed.ExchangeTimestampMilliseconds.Value > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                feed.ExchangeTimestampMilliseconds.Value);
        }

        return receivedAtUtc;
    }

    private static IReadOnlyCollection<CanonicalLiveCandleV1> NormalizeCandles(
        IReadOnlyCollection<UpstoxDecodedCandle>? candles)
    {
        if (candles is null || candles.Count == 0)
        {
            return Array.Empty<CanonicalLiveCandleV1>();
        }

        var normalized = new List<CanonicalLiveCandleV1>(candles.Count);

        foreach (var candle in candles)
        {
            var timeframe = MapInterval(candle.Interval);
            if (timeframe is null ||
                !DateTimeOffset.TryParse(
                    candle.Timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var openAtUtc))
            {
                continue;
            }

            normalized.Add(new CanonicalLiveCandleV1(
                timeframe,
                openAtUtc.ToUniversalTime(),
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close,
                candle.Volume));
        }

        return normalized;
    }

    private static string? MapInterval(string interval) =>
        interval.Trim().ToUpperInvariant() switch
        {
            "I1" or "1M" or "1MIN" => "1m",
            "I5" or "5M" or "5MIN" => "5m",
            "I15" or "15M" or "15MIN" => "15m",
            "I60" or "1H" or "60MIN" => "1h",
            "1D" or "D1" or "DAY" => "1d",
            _ => null,
        };

    private static string BuildEventId(
        UpstoxDecodedMarketFeed feed,
        DateTimeOffset eventAtUtc,
        string rawPayload)
    {
        if (!string.IsNullOrWhiteSpace(feed.SourceSequence))
        {
            return $"{feed.InstrumentKey}|{feed.SourceSequence}";
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawPayload));
        var suffix = Convert.ToHexString(hash[..8]);
        return $"{feed.InstrumentKey}|{eventAtUtc:O}|{suffix}";
    }
}
