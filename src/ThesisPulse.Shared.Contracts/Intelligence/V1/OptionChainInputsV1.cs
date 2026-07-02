namespace ThesisPulse.Shared.Contracts.Intelligence.V1;

public sealed record OptionChainStrikeInputV1(
    decimal StrikePrice,
    decimal CallOpenInterest,
    decimal PutOpenInterest,
    decimal CallOpenInterestChange,
    decimal PutOpenInterestChange,
    decimal CallVolume,
    decimal PutVolume,
    decimal? CallImpliedVolatility,
    decimal? PutImpliedVolatility,
    decimal? CallLastPrice,
    decimal? PutLastPrice,
    decimal? CallPriceChange,
    decimal? PutPriceChange);

public sealed record OptionChainExpiryInputV1(
    DateOnly ExpiryDate,
    decimal UnderlyingPrice,
    IReadOnlyCollection<OptionChainStrikeInputV1> Strikes);

public sealed record OptionChainEvaluationInputV1(
    Guid RequestUid,
    Guid SourceSnapshotUid,
    string InstrumentCode,
    string Environment,
    DateTimeOffset SourceAsOfUtc,
    DateTimeOffset EvaluatedAtUtc,
    string EngineVersion,
    IReadOnlyCollection<OptionChainExpiryInputV1> Expiries);
