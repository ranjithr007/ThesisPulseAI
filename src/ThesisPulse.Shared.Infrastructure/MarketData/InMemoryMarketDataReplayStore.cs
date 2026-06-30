using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class InMemoryMarketDataReplayStore(
    InMemoryDispatchTarget dispatchTarget) : IMarketDataReplayStore
{
    public Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        long afterPosition,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (afterPosition < 0 || maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyCollection<OutboxMessage> result = dispatchTarget.PublishedMessages
            .Where(message => MarketDataPublicationContractV1.EventTypes.Contains(
                message.Metadata.EventType))
            .Select((message, index) => message with { StreamPosition = index + 1L })
            .Where(message => message.StreamPosition > afterPosition)
            .Take(maximumCount)
            .ToArray();
        return Task.FromResult(result);
    }
}
