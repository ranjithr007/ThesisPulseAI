using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public interface IMarketDataConsumerSink
{
    Task ApplyAsync(
        MarketQuotePublishedV1 value,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        MarketCandlePublishedV1 value,
        CancellationToken cancellationToken);
}

public sealed class MarketDataConsumerBuffer : IMarketDataConsumerSink
{
    private readonly ConcurrentDictionary<string, MarketQuotePublishedV1> _quotes = new();
    private readonly ConcurrentDictionary<string, MarketCandlePublishedV1> _candles = new();

    public Task ApplyAsync(
        MarketQuotePublishedV1 value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _quotes[value.InstrumentKey] = value;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(
        MarketCandlePublishedV1 value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _candles[$"{value.InstrumentKey}|{value.Timeframe}"] = value;
        return Task.CompletedTask;
    }

    public object GetLatest(string instrumentKey) => new
    {
        quote = _quotes.TryGetValue(instrumentKey, out var quote) ? quote : null,
        candles = _candles.Values
            .Where(value => value.InstrumentKey.Equals(
                instrumentKey,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.Timeframe)
            .ToArray(),
    };

    public int QuoteCount => _quotes.Count;
    public int CandleCount => _candles.Count;
}
