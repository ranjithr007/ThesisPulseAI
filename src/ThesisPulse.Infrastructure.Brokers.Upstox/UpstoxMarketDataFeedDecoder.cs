using System.Text;
using Google.Protobuf;
using ThesisPulse.Infrastructure.Brokers.Upstox.Protobuf;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxDecodedFeedEnvelope(
    string MessageType,
    long CurrentTimestampMilliseconds,
    IReadOnlyDictionary<string, string> SegmentStatuses,
    IReadOnlyCollection<UpstoxDecodedMarketFeed> Feeds,
    string RawPayloadJson);

public interface IUpstoxMarketDataFeedDecoder
{
    UpstoxDecodedFeedEnvelope Decode(ReadOnlyMemory<byte> payload);
}

public sealed class UpstoxMarketDataFeedDecoder : IUpstoxMarketDataFeedDecoder
{
    public UpstoxDecodedFeedEnvelope Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("Upstox feed payload cannot be empty.");
        }

        FeedResponse response;
        try
        {
            response = FeedResponse.Parser.ParseFrom(payload.ToArray());
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException(
                "Upstox feed payload is not valid Market Data Feed V3 protobuf.",
                exception);
        }

        var messageType = MapMessageType(response.Type);
        var rawPayloadJson = JsonFormatter.Default.Format(response);
        var segmentStatuses = response.MarketInfo?.SegmentStatus
            .ToDictionary(
                pair => pair.Key,
                pair => ToUpperSnakeCase(pair.Value.ToString()),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var feeds = new List<UpstoxDecodedMarketFeed>(response.Feeds.Count);

        foreach (var pair in response.Feeds)
        {
            var decoded = DecodeFeed(
                pair.Key,
                pair.Value,
                response.CurrentTs,
                messageType,
                rawPayloadJson);

            if (decoded is not null)
            {
                feeds.Add(decoded);
            }
        }

        return new UpstoxDecodedFeedEnvelope(
            messageType,
            response.CurrentTs,
            segmentStatuses,
            feeds,
            rawPayloadJson);
    }

    private static UpstoxDecodedMarketFeed? DecodeFeed(
        string instrumentKey,
        Feed feed,
        long currentTimestampMilliseconds,
        string messageType,
        string rawPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(instrumentKey))
        {
            return null;
        }

        LTPC? ltpc = null;
        MarketOHLC? marketOhlc = null;
        decimal? openInterest = null;
        decimal? totalBuyQuantity = null;
        decimal? totalSellQuantity = null;

        if (feed.Ltpc is not null)
        {
            ltpc = feed.Ltpc;
        }
        else if (feed.FullFeed is not null)
        {
            if (feed.FullFeed.MarketFF is not null)
            {
                var marketFeed = feed.FullFeed.MarketFF;
                ltpc = marketFeed.Ltpc;
                marketOhlc = marketFeed.MarketOHLC;
                openInterest = ToDecimal(marketFeed.Oi);
                totalBuyQuantity = ToDecimal(marketFeed.Tbq);
                totalSellQuantity = ToDecimal(marketFeed.Tsq);
            }
            else if (feed.FullFeed.IndexFF is not null)
            {
                var indexFeed = feed.FullFeed.IndexFF;
                ltpc = indexFeed.Ltpc;
                marketOhlc = indexFeed.MarketOHLC;
            }
        }
        else if (feed.FirstLevelWithGreeks is not null)
        {
            var firstLevel = feed.FirstLevelWithGreeks;
            ltpc = firstLevel.Ltpc;
            openInterest = ToDecimal(firstLevel.Oi);
        }

        if (ltpc is null && marketOhlc is null)
        {
            return null;
        }

        var candles = marketOhlc?.Ohlc
            .Select(candle => new UpstoxDecodedCandle(
                candle.Interval,
                DateTimeOffset.FromUnixTimeMilliseconds(candle.Ts).ToString("O"),
                ToDecimal(candle.Open),
                ToDecimal(candle.High),
                ToDecimal(candle.Low),
                ToDecimal(candle.Close),
                candle.Vol))
            .ToArray()
            ?? Array.Empty<UpstoxDecodedCandle>();

        return new UpstoxDecodedMarketFeed(
            instrumentKey,
            MapRequestMode(feed.RequestMode.ToString()),
            currentTimestampMilliseconds > 0
                ? currentTimestampMilliseconds
                : null,
            ltpc is not null ? ToDecimal(ltpc.Ltp) : null,
            ltpc is not null ? ltpc.Ltq : null,
            ltpc is not null && ltpc.Ltt > 0 ? ltpc.Ltt : null,
            ltpc is not null ? ToDecimal(ltpc.Cp) : null,
            openInterest,
            totalBuyQuantity,
            totalSellQuantity,
            candles,
            $"{currentTimestampMilliseconds}:{messageType}",
            rawPayloadJson);
    }

    private static string MapMessageType(Protobuf.Type type) =>
        ToUpperSnakeCase(type.ToString()).ToLowerInvariant();

    private static string MapRequestMode(string mode)
    {
        var normalized = ToUpperSnakeCase(mode).ToLowerInvariant();
        return normalized == "full_d5" ? UpstoxLiveFeedModes.Full : normalized;
    }

    private static decimal ToDecimal(double value) => Convert.ToDecimal(value);

    private static decimal ToDecimal(long value) => value;

    private static string ToUpperSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 &&
                value[index - 1] != '_')
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }
}
