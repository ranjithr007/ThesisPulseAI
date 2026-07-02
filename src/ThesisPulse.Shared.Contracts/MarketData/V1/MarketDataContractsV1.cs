namespace ThesisPulse.Shared.Contracts.MarketData.V1;

public static class MarketDataContractV1
{
    public const string ContractVersion = "1.0.0";

    public static readonly IReadOnlySet<string> Timeframes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1m",
            "5m",
            "15m",
            "1h",
            "1d",
        };

    public static TimeSpan GetDuration(string timeframe) => timeframe switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "1d" => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(timeframe),
            timeframe,
            "Unsupported market-data timeframe."),
    };
}

public static class MarketDataQualityStatusV1
{
    public const string Valid = "VALID";
    public const string Degraded = "DEGRADED";
    public const string Stale = "STALE";
    public const string Incomplete = "INCOMPLETE";
    public const string Duplicate = "DUPLICATE";
    public const string OutOfOrder = "OUT_OF_ORDER";
    public const string Conflicted = "CONFLICTED";
    public const string Invalid = "INVALID";
    public const string Unknown = "UNKNOWN";
}

public sealed record CanonicalInstrumentV1(
    string ProviderCode,
    string ProviderInstrumentKey,
    string ExchangeCode,
    string ProviderSegment,
    string CanonicalSymbol,
    string DisplayName,
    string InstrumentType,
    string MarketSegment,
    string? Isin,
    string? UnderlyingProviderInstrumentKey,
    DateOnly? ExpiryDate,
    decimal? StrikePrice,
    string? OptionType,
    decimal TickSize,
    decimal LotSize,
    decimal? FreezeQuantity,
    bool IsTradeAllowed,
    bool IsShortAllowed,
    DateOnly EffectiveFromDate,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record InstrumentSynchronizationResultV1(
    string ProviderCode,
    DateTimeOffset SnapshotReceivedAtUtc,
    int Received,
    int Created,
    int Updated,
    int MappingCreated,
    int MappingUpdated,
    int Skipped,
    IReadOnlyCollection<string> Warnings);

public sealed record HistoricalCandleRequestV1(
    string ProviderInstrumentKey,
    string Timeframe,
    DateOnly FromDate,
    DateOnly ToDate,
    string CorrelationId);

public sealed record CanonicalCandleV1(
    string ProviderCode,
    string ProviderInstrumentKey,
    string SourceEventId,
    string Timeframe,
    DateTimeOffset OpenAtUtc,
    DateTimeOffset CloseAtUtc,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal VolumeQuantity,
    decimal? OpenInterest,
    long? TradeCount,
    decimal? VwapPrice,
    bool IsClosed,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    string SourceVersion,
    string RawPayloadJson);

public sealed record HistoricalCandleIngestionResultV1(
    Guid IngestionBatchUid,
    string ProviderCode,
    string ProviderInstrumentKey,
    string Timeframe,
    DateOnly FromDate,
    DateOnly ToDate,
    int Received,
    int Accepted,
    int Duplicates,
    int Rejected,
    string Status,
    IReadOnlyCollection<string> Warnings);

public sealed record CanonicalLiveCandleV1(
    string Timeframe,
    DateTimeOffset OpenAtUtc,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal VolumeQuantity);

public sealed record CanonicalLiveMarketUpdateV1(
    string ProviderCode,
    string ProviderInstrumentKey,
    string SourceEventId,
    DateTimeOffset EventAtUtc,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal? LastTradedPrice,
    decimal? LastTradedQuantity,
    decimal? PreviousClosePrice,
    decimal? OpenInterest,
    decimal? TotalBuyQuantity,
    decimal? TotalSellQuantity,
    IReadOnlyCollection<CanonicalLiveCandleV1> CandleSnapshots,
    string SourceVersion,
    string RawPayloadJson);

public sealed record LiveMarketIngestionResultV1(
    Guid IngestionBatchUid,
    string ProviderCode,
    int Received,
    int Accepted,
    int Duplicates,
    int Rejected,
    string Status,
    IReadOnlyCollection<string> Warnings);

public sealed record MarketDataFreshnessAssessmentV1(
    string QualityStatus,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset FreshnessBasisUtc,
    long AgeMilliseconds,
    long MaximumAgeMilliseconds,
    bool IsUsableForNewExposure,
    bool IsUsableForExit,
    string PolicyVersion,
    IReadOnlyCollection<string> ReasonCodes);

public sealed record StoredCandleV1(
    long CandleId,
    Guid CandleUid,
    string InstrumentKey,
    string Timeframe,
    DateTimeOffset OpenAtUtc,
    DateTimeOffset CloseAtUtc,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal VolumeQuantity,
    string QualityStatus,
    bool IsUsableForNewExposure,
    DateTimeOffset ReceivedAtUtc)
{
    public bool IsClosed { get; init; } = true;
}
