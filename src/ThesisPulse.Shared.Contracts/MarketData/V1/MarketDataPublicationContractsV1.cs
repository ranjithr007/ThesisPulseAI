namespace ThesisPulse.Shared.Contracts.MarketData.V1;

public static class MarketDataPublicationContractV1
{
    public const string ContractVersion = "1.0";
    public const string QuoteEventType = "market.quote.published.v1";
    public const string CandleEventType = "market.candle.published.v1";
    public const string OptionChainEventType = "market.option_chain.published.v1";

    public static readonly IReadOnlySet<string> EventTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            QuoteEventType,
            CandleEventType,
            OptionChainEventType,
        };
}

public sealed record MarketQuotePublishedV1(
    string ProviderCode,
    string InstrumentKey,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal? LastTradedPrice,
    decimal? LastTradedQuantity,
    decimal? PreviousClosePrice,
    decimal? OpenInterest,
    decimal? TotalBuyQuantity,
    decimal? TotalSellQuantity,
    string QualityStatus,
    bool IsUsableForNewExposure,
    string SourceVersion);

public sealed record MarketCandlePublishedV1(
    string ProviderCode,
    string InstrumentKey,
    string Timeframe,
    DateTimeOffset OpenAtUtc,
    DateTimeOffset CloseAtUtc,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal VolumeQuantity,
    decimal? OpenInterest,
    bool IsClosed,
    bool IsProvisional,
    int Revision,
    string QualityStatus,
    bool IsUsableForNewExposure,
    DateTimeOffset ReceivedAtUtc,
    string SourceVersion);

public sealed record MarketOptionChainEntryPublishedV1(
    Guid DerivativeContractUid,
    string InstrumentKey,
    DateOnly ExpiryDate,
    decimal StrikePrice,
    string OptionType,
    decimal? LastPrice,
    decimal? VolumeQuantity,
    decimal? OpenInterest,
    decimal? ImpliedVolatility,
    decimal? Delta,
    decimal ContractMultiplier,
    string QualityStatus,
    string? GreeksSourceVersion);

public sealed record MarketOptionChainPublishedV1(
    Guid SnapshotUid,
    string UnderlyingInstrumentKey,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    string SnapshotStatus,
    string QualityStatus,
    bool IsPointInTimeEligible,
    int Revision,
    IReadOnlyCollection<MarketOptionChainEntryPublishedV1> Entries,
    string? CalculationSourceVersion);

public sealed record OptionChainIntelligenceIntakeV1(
    Guid SourceMessageUid,
    Guid SnapshotUid,
    string UnderlyingInstrumentKey,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    string SnapshotStatus,
    string QualityStatus,
    bool IsPointInTimeEligible,
    int Revision,
    IReadOnlyCollection<MarketOptionChainEntryPublishedV1> Entries,
    string? CalculationSourceVersion);

public sealed record MarketDataDeliveryV1<TPayload>(
    long StreamPosition,
    ThesisPulse.Shared.Contracts.Messaging.V1.EventEnvelope<TPayload> Envelope)
    where TPayload : class;

public sealed record MarketDataReplayPageV1(
    long AfterPosition,
    long LastPosition,
    int Count,
    bool HasMore,
    IReadOnlyCollection<MarketDataReplayItemV1> Items);

public sealed record MarketDataReplayItemV1(
    long StreamPosition,
    string EventType,
    string ContractVersion,
    Guid MessageId,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string? CausationId,
    string Producer,
    string ProducerVersion,
    string Environment,
    string ConfigurationVersion,
    string PayloadJson);
