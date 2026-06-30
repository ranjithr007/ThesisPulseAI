using System.Collections.Concurrent;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class InMemoryMarketDataConsumerCheckpointStore :
    IMarketDataConsumerCheckpointStore
{
    private readonly ConcurrentDictionary<string, MarketDataConsumerCheckpoint> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<MarketDataConsumerCheckpoint?> GetAsync(
        string consumerName,
        string streamName,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(BuildKey(consumerName, streamName, partitionKey), out var value);
        return Task.FromResult(value);
    }

    public Task AdvanceAsync(
        MarketDataConsumerCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        var key = BuildKey(
            checkpoint.ConsumerName,
            checkpoint.StreamName,
            checkpoint.PartitionKey);
        _items.AddOrUpdate(
            key,
            checkpoint,
            (_, current) => checkpoint.LastPosition > current.LastPosition
                ? checkpoint
                : current);
        return Task.CompletedTask;
    }

    private static string BuildKey(
        string consumerName,
        string streamName,
        string partitionKey) =>
        $"{consumerName}|{streamName}|{partitionKey}";
}
