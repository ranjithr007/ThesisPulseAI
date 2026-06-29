using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class InMemoryMarketDataStore(
    IMarketDataFreshnessEvaluator freshnessEvaluator) :
    IInstrumentCatalogStore,
    IMarketDataStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CanonicalInstrumentV1> _instruments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CanonicalCandleV1> _candles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveEventIds =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<InstrumentSynchronizationResultV1> SynchronizeAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instruments);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var warnings = new List<string>();

        lock (_sync)
        {
            foreach (var instrument in instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument.ProviderInstrumentKey) ||
                    string.IsNullOrWhiteSpace(instrument.CanonicalSymbol))
                {
                    skipped++;
                    warnings.Add("Instrument with missing provider key or symbol was skipped.");
                    continue;
                }

                if (_instruments.ContainsKey(instrument.ProviderInstrumentKey))
                {
                    _instruments[instrument.ProviderInstrumentKey] = instrument;
                    updated++;
                }
                else
                {
                    _instruments.Add(instrument.ProviderInstrumentKey, instrument);
                    created++;
                }
            }
        }

        var providerCode = instruments.FirstOrDefault()?.ProviderCode ?? "UNKNOWN";
        return Task.FromResult(new InstrumentSynchronizationResultV1(
            providerCode,
            snapshotReceivedAtUtc,
            instruments.Count,
            created,
            updated,
            created,
            updated,
            skipped,
            warnings));
    }

    public Task<HistoricalCandleIngestionResultV1> PersistHistoricalCandlesAsync(
        HistoricalCandleRequestV1 request,
        IReadOnlyCollection<CanonicalCandleV1> candles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candles);

        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var warnings = new List<string>();

        lock (_sync)
        {
            foreach (var candle in candles)
            {
                var assessment = freshnessEvaluator.EvaluateCandle(
                    candle,
                    candle.ReceivedAtUtc);

                if (assessment.QualityStatus == MarketDataQualityStatusV1.Invalid)
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected invalid candle '{candle.SourceEventId}'.");
                    continue;
                }

                var identity = BuildCandleIdentity(candle);
                if (!_candles.TryAdd(identity, candle))
                {
                    duplicates++;
                    continue;
                }

                accepted++;
            }
        }

        var status = rejected == 0
            ? "SUCCEEDED"
            : accepted > 0
                ? "PARTIAL"
                : "FAILED";

        return Task.FromResult(new HistoricalCandleIngestionResultV1(
            Guid.NewGuid(),
            candles.FirstOrDefault()?.ProviderCode ?? "UNKNOWN",
            request.ProviderInstrumentKey,
            request.Timeframe,
            request.FromDate,
            request.ToDate,
            candles.Count,
            accepted,
            duplicates,
            rejected,
            status,
            warnings));
    }

    public Task<LiveMarketIngestionResultV1> PersistLiveUpdatesAsync(
        IReadOnlyCollection<CanonicalLiveMarketUpdateV1> updates,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var warnings = new List<string>();

        lock (_sync)
        {
            foreach (var update in updates)
            {
                var assessment = freshnessEvaluator.EvaluateLiveUpdate(
                    update,
                    update.ReceivedAtUtc);

                if (assessment.QualityStatus == MarketDataQualityStatusV1.Invalid)
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected invalid live event '{update.SourceEventId}'.");
                    continue;
                }

                if (!_liveEventIds.Add(update.SourceEventId))
                {
                    duplicates++;
                    continue;
                }

                accepted++;
            }
        }

        var status = rejected == 0
            ? "SUCCEEDED"
            : accepted > 0
                ? "PARTIAL"
                : "FAILED";

        return Task.FromResult(new LiveMarketIngestionResultV1(
            Guid.NewGuid(),
            updates.FirstOrDefault()?.ProviderCode ?? "UNKNOWN",
            updates.Count,
            accepted,
            duplicates,
            rejected,
            status,
            warnings));
    }

    public Task<IReadOnlyCollection<StoredCandleV1>> GetLatestCandlesAsync(
        string instrumentKey,
        string timeframe,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentKey);

        if (!MarketDataContractV1.Timeframes.Contains(timeframe))
        {
            throw new ArgumentOutOfRangeException(nameof(timeframe));
        }

        if (maximumCount is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            IReadOnlyCollection<StoredCandleV1> result = _candles.Values
                .Where(candle =>
                    candle.ProviderInstrumentKey.Equals(
                        instrumentKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    candle.Timeframe.Equals(
                        timeframe,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candle => candle.OpenAtUtc)
                .Take(maximumCount)
                .Select((candle, index) =>
                {
                    var assessment = freshnessEvaluator.EvaluateCandle(
                        candle,
                        DateTimeOffset.UtcNow);

                    return new StoredCandleV1(
                        CandleId: index + 1,
                        CandleUid: CreateStableGuid(BuildCandleIdentity(candle)),
                        InstrumentKey: candle.ProviderInstrumentKey,
                        Timeframe: candle.Timeframe,
                        OpenAtUtc: candle.OpenAtUtc,
                        CloseAtUtc: candle.CloseAtUtc,
                        OpenPrice: candle.OpenPrice,
                        HighPrice: candle.HighPrice,
                        LowPrice: candle.LowPrice,
                        ClosePrice: candle.ClosePrice,
                        VolumeQuantity: candle.VolumeQuantity,
                        QualityStatus: assessment.QualityStatus,
                        IsUsableForNewExposure:
                            assessment.IsUsableForNewExposure,
                        ReceivedAtUtc: candle.ReceivedAtUtc);
                })
                .ToArray();

            return Task.FromResult(result);
        }
    }

    private static string BuildCandleIdentity(CanonicalCandleV1 candle) =>
        $"{candle.ProviderCode}|{candle.ProviderInstrumentKey}|" +
        $"{candle.Timeframe}|{candle.OpenAtUtc:O}";

    private static Guid CreateStableGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash[..16]);
    }
}
