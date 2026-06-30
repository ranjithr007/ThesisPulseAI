using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public interface IMarketDataReplayStore
{
    Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        long afterPosition,
        int maximumCount,
        CancellationToken cancellationToken = default);
}
