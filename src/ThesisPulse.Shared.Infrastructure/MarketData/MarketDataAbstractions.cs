using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public interface IMarketDataProvider
{
    string ProviderCode { get; }

    Task<IReadOnlyCollection<CanonicalInstrumentV1>> GetInstrumentSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<CanonicalCandleV1>> GetHistoricalCandlesAsync(
        HistoricalCandleRequestV1 request,
        CancellationToken cancellationToken = default);

    Task<Uri> GetLiveFeedAuthorizedUriAsync(
        CancellationToken cancellationToken = default);
}

public interface IInstrumentCatalogStore
{
    Task<InstrumentSynchronizationResultV1> SynchronizeAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IMarketDataStore
{
    Task<HistoricalCandleIngestionResultV1> PersistHistoricalCandlesAsync(
        HistoricalCandleRequestV1 request,
        IReadOnlyCollection<CanonicalCandleV1> candles,
        CancellationToken cancellationToken = default);

    Task<LiveMarketIngestionResultV1> PersistLiveUpdatesAsync(
        IReadOnlyCollection<CanonicalLiveMarketUpdateV1> updates,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StoredCandleV1>> GetLatestCandlesAsync(
        string instrumentKey,
        string timeframe,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public interface IMarketDataFreshnessEvaluator
{
    MarketDataFreshnessAssessmentV1 EvaluateCandle(
        CanonicalCandleV1 candle,
        DateTimeOffset evaluatedAtUtc);

    MarketDataFreshnessAssessmentV1 EvaluateLiveUpdate(
        CanonicalLiveMarketUpdateV1 update,
        DateTimeOffset evaluatedAtUtc);
}
