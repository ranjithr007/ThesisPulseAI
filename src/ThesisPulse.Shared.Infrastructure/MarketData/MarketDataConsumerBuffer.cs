using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class MarketDataConsumerBuffer
{
    private readonly ConcurrentDictionary<string, MarketQuotePublishedV1> _quotes = new();
    private readonly ConcurrentDictionary<string, MarketCandlePublishedV1> _candles = new();

    public void Apply(MarketQuotePublishedV1 value) =>
        _quotes[value.InstrumentKey] = value;

    public void Apply(MarketCandlePublishedV1 value) =>
        _candles[$"{value.InstrumentKey}|{value.Timeframe}"] = value;

    public int QuoteCount => _quotes.Count;
    public int CandleCount => _candles.Count;
}
