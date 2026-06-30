namespace ThesisPulse.Shared.Contracts.MarketData.V1;

public static class DerivativesMarketDataContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string CatalogPolicyVersion = "derivatives-catalog-v1.0.0";
    public const string BasisPolicyVersion = "futures-basis-v1.0.0";
    public const string OptionChainPolicyVersion = "option-chain-normalization-v1.0.0";

    public static readonly IReadOnlySet<string> ContractClasses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INDEX_FUTURE",
            "STOCK_FUTURE",
            "INDEX_OPTION",
            "STOCK_OPTION",
        };

    public static readonly IReadOnlySet<string> ExpiryTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WEEKLY",
            "MONTHLY",
            "QUARTERLY",
            "OTHER",
            "UNKNOWN",
        };

    public static readonly IReadOnlySet<string> SettlementTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CASH",
            "PHYSICAL",
            "UNKNOWN",
        };
}

public sealed record DerivativeContractReferenceV1(
    Guid DerivativeContractUid,
    Guid InstrumentUid,
    string CanonicalSymbol,
    Guid UnderlyingInstrumentUid,
    string UnderlyingCanonicalSymbol,
    string ContractClass,
    DateOnly ExpiryDate,
    string ExpiryType,
    DateOnly LastTradingDate,
    DateOnly? SettlementDate,
    DateOnly? RolloverStartDate,
    string SettlementType,
    decimal ContractMultiplier,
    decimal LotSize,
    decimal? StrikePrice,
    string? OptionType,
    string Status,
    bool SelectionEligible,
    DateOnly ValidFromDate,
    DateOnly? ValidToDate,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record DerivativeExpiryReferenceV1(
    Guid DerivativeExpiryScheduleUid,
    Guid UnderlyingInstrumentUid,
    string UnderlyingCanonicalSymbol,
    string ExchangeCode,
    string MarketSegment,
    DateOnly ExpiryDate,
    string ExpiryType,
    DateOnly LastTradingDate,
    DateOnly? SettlementDate,
    DateOnly? RolloverStartDate,
    string Status,
    string CalendarVersion,
    DateOnly ValidFromDate,
    DateOnly? ValidToDate);

public sealed record DerivativeContractSynchronizationResultV1(
    string ProviderCode,
    DateTimeOffset SnapshotReceivedAtUtc,
    int ReceivedDerivatives,
    int Created,
    int Updated,
    int ExpirySchedulesCreated,
    int ExpirySchedulesUpdated,
    int Skipped,
    IReadOnlyCollection<string> Warnings);

public sealed record CanonicalFuturesBasisObservationV1(
    string ProviderCode,
    string SourceEventId,
    int Revision,
    string UnderlyingProviderInstrumentKey,
    string FutureProviderInstrumentKey,
    DateTimeOffset EventAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    decimal FuturePrice,
    string SourceVersion,
    string CorrelationId,
    string RawPayloadJson);

public sealed record StoredFuturesBasisObservationV1(
    Guid ObservationUid,
    Guid UnderlyingInstrumentUid,
    string UnderlyingCanonicalSymbol,
    Guid FutureInstrumentUid,
    string FutureCanonicalSymbol,
    Guid DerivativeContractUid,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    decimal FuturePrice,
    decimal BasisAmount,
    decimal BasisFraction,
    int DaysToExpiry,
    decimal? AnnualizedBasisFraction,
    string QualityStatus,
    bool IsPointInTimeEligible,
    int Revision,
    string SourceVersion,
    string PolicyVersion);

public sealed record FuturesBasisIngestionResultV1(
    string Outcome,
    StoredFuturesBasisObservationV1? Observation,
    IReadOnlyCollection<string> Warnings);

public sealed record CanonicalOptionChainEntryV1(
    string ProviderInstrumentKey,
    DateTimeOffset QuoteAtUtc,
    decimal StrikePrice,
    string OptionType,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? LastPrice,
    decimal? BidQuantity,
    decimal? AskQuantity,
    decimal? VolumeQuantity,
    decimal? OpenInterest,
    decimal? PreviousOpenInterest,
    decimal? ImpliedVolatility,
    decimal? Delta,
    decimal? Gamma,
    decimal? Theta,
    decimal? Vega,
    decimal? Rho,
    string? GreeksSourceVersion,
    string QualityStatus,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record CanonicalOptionChainSnapshotV1(
    string ProviderCode,
    string SourceEventId,
    int Revision,
    string UnderlyingProviderInstrumentKey,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    string SourceVersion,
    string? CalculationSourceVersion,
    string CorrelationId,
    IReadOnlyCollection<CanonicalOptionChainEntryV1> Entries,
    string RawPayloadJson);

public sealed record StoredOptionChainEntryV1(
    Guid DerivativeContractUid,
    Guid InstrumentUid,
    string CanonicalSymbol,
    DateTimeOffset QuoteAtUtc,
    decimal StrikePrice,
    string OptionType,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? LastPrice,
    decimal? BidQuantity,
    decimal? AskQuantity,
    decimal? VolumeQuantity,
    decimal? OpenInterest,
    decimal? PreviousOpenInterest,
    decimal? OpenInterestChange,
    decimal? ImpliedVolatility,
    decimal? Delta,
    decimal? Gamma,
    decimal? Theta,
    decimal? Vega,
    decimal? Rho,
    string? GreeksSourceVersion,
    string QualityStatus,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record StoredOptionChainSnapshotV1(
    Guid SnapshotUid,
    Guid UnderlyingInstrumentUid,
    string UnderlyingCanonicalSymbol,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    string SnapshotStatus,
    string QualityStatus,
    bool IsPointInTimeEligible,
    int Revision,
    string SourceVersion,
    string? CalculationSourceVersion,
    string PolicyVersion,
    IReadOnlyCollection<StoredOptionChainEntryV1> Entries,
    IReadOnlyCollection<string> Warnings);

public sealed record OptionChainIngestionResultV1(
    string Outcome,
    StoredOptionChainSnapshotV1? Snapshot,
    int ReceivedContracts,
    int AcceptedContracts,
    int RejectedContracts,
    IReadOnlyCollection<string> Warnings);
