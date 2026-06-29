using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public sealed record MarketDataOperationsOptions
{
    public bool Enabled { get; init; }

    public string? InternalApiKey { get; init; }

    public int MaximumHistoricalDays { get; init; } = 366;

    public void Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(InternalApiKey))
        {
            throw new InvalidOperationException(
                "MarketData:Operations:InternalApiKey is required when operations are enabled.");
        }

        if (MaximumHistoricalDays is < 1 or > 3650)
        {
            throw new InvalidOperationException(
                "Market-data maximum historical days must be between 1 and 3650.");
        }
    }
}

public sealed record MarketDataJobSnapshot(
    string Operation,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? CorrelationId,
    int Received,
    int Accepted,
    int Duplicates,
    int Rejected,
    string? Error);

public sealed class MarketDataJobState
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MarketDataJobSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<MarketDataJobSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _snapshots.Values
                .OrderBy(snapshot => snapshot.Operation)
                .ToArray();
        }
    }

    public void Started(string operation, string correlationId)
    {
        lock (_sync)
        {
            _snapshots[operation] = new MarketDataJobSnapshot(
                operation,
                "RUNNING",
                DateTimeOffset.UtcNow,
                CompletedAtUtc: null,
                correlationId,
                Received: 0,
                Accepted: 0,
                Duplicates: 0,
                Rejected: 0,
                Error: null);
        }
    }

    public void Completed(
        string operation,
        int received,
        int accepted,
        int duplicates,
        int rejected)
    {
        lock (_sync)
        {
            var current = _snapshots[operation];
            _snapshots[operation] = current with
            {
                Status = "COMPLETED",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Received = received,
                Accepted = accepted,
                Duplicates = duplicates,
                Rejected = rejected,
                Error = null,
            };
        }
    }

    public void Failed(string operation, string error)
    {
        lock (_sync)
        {
            var current = _snapshots[operation];
            _snapshots[operation] = current with
            {
                Status = "FAILED",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = error,
            };
        }
    }
}

public sealed class MarketDataOrchestrator(
    IMarketDataProvider provider,
    IInstrumentCatalogStore instrumentCatalogStore,
    IMarketDataStore marketDataStore,
    IUpstoxLiveFeedNormalizer liveFeedNormalizer,
    MarketDataOperationsOptions options,
    MarketDataJobState jobState)
{
    public async Task<InstrumentSynchronizationResultV1> SynchronizeInstrumentsAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "INSTRUMENT_SYNCHRONIZATION";
        jobState.Started(operation, correlationId);

        try
        {
            var receivedAtUtc = DateTimeOffset.UtcNow;
            var instruments = await provider.GetInstrumentSnapshotAsync(cancellationToken);
            var result = await instrumentCatalogStore.SynchronizeAsync(
                instruments,
                receivedAtUtc,
                cancellationToken);
            jobState.Completed(
                operation,
                result.Received,
                result.Created + result.Updated,
                0,
                result.Skipped);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            jobState.Failed(operation, exception.Message);
            throw;
        }
    }

    public async Task<HistoricalCandleIngestionResultV1> BackfillAsync(
        HistoricalCandleRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "HISTORICAL_CANDLE_BACKFILL";
        ValidateBackfillRequest(request);
        jobState.Started(operation, request.CorrelationId);

        try
        {
            var candles = await provider.GetHistoricalCandlesAsync(
                request,
                cancellationToken);
            var result = await marketDataStore.PersistHistoricalCandlesAsync(
                request,
                candles,
                cancellationToken);
            jobState.Completed(
                operation,
                result.Received,
                result.Accepted,
                result.Duplicates,
                result.Rejected);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            jobState.Failed(operation, exception.Message);
            throw;
        }
    }

    public async Task<LiveMarketIngestionResultV1> NormalizeAndPersistLiveAsync(
        IReadOnlyCollection<UpstoxDecodedMarketFeed> feeds,
        DateTimeOffset receivedAtUtc,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "LIVE_FEED_NORMALIZATION";
        jobState.Started(operation, correlationId);

        try
        {
            var updates = liveFeedNormalizer.Normalize(feeds, receivedAtUtc);
            var result = await marketDataStore.PersistLiveUpdatesAsync(
                updates,
                correlationId,
                cancellationToken);
            jobState.Completed(
                operation,
                result.Received,
                result.Accepted,
                result.Duplicates,
                result.Rejected);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            jobState.Failed(operation, exception.Message);
            throw;
        }
    }

    private void ValidateBackfillRequest(HistoricalCandleRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ToDate.DayNumber - request.FromDate.DayNumber >
            options.MaximumHistoricalDays)
        {
            throw new ArgumentException(
                $"Historical request exceeds the configured {options.MaximumHistoricalDays}-day limit.");
        }
    }
}
