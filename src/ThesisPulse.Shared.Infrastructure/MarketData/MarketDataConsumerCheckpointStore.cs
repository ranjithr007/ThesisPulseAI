namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record MarketDataConsumerCheckpoint(
    string ConsumerName,
    string StreamName,
    string PartitionKey,
    long LastPosition,
    Guid LastMessageId,
    DateTimeOffset LastOccurredAtUtc);

public interface IMarketDataConsumerCheckpointStore
{
    Task<MarketDataConsumerCheckpoint?> GetAsync(
        string consumerName,
        string streamName,
        string partitionKey,
        CancellationToken cancellationToken = default);

    Task AdvanceAsync(
        MarketDataConsumerCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}
