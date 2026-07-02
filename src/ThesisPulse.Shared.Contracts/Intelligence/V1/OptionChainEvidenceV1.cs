namespace ThesisPulse.Shared.Contracts.Intelligence.V1;

public sealed record OptionChainOpenInterestWallV1(
    DateOnly ExpiryDate,
    decimal StrikePrice,
    string Side,
    decimal OpenInterest,
    decimal DistanceFromUnderlyingFraction);

public sealed record OptionChainStrikeActivityV1(
    DateOnly ExpiryDate,
    decimal StrikePrice,
    string OptionSide,
    string Classification,
    decimal OpenInterestChange,
    decimal Volume,
    IReadOnlyCollection<string> Reasons);

public sealed record OptionChainIvStructureV1(
    DateOnly ExpiryDate,
    decimal? AtTheMoneyCallIv,
    decimal? AtTheMoneyPutIv,
    decimal? PutCallSkew,
    decimal? WingSkew,
    decimal? TermPremiumToNextExpiry);

public sealed record OptionChainIntelligenceEvidenceV1(
    Guid EvidenceUid,
    Guid RequestUid,
    Guid SourceSnapshotUid,
    string ContractVersion,
    string EngineVersion,
    string InstrumentCode,
    string Environment,
    DateTimeOffset SourceAsOfUtc,
    DateTimeOffset EvaluatedAtUtc,
    decimal PutCallRatioByOpenInterest,
    decimal PutCallRatioByVolume,
    decimal MaxPainStrike,
    string DirectionalBias,
    decimal BullishScore,
    decimal BearishScore,
    decimal Confidence,
    IReadOnlyCollection<OptionChainOpenInterestWallV1> OpenInterestWalls,
    IReadOnlyCollection<OptionChainStrikeActivityV1> StrikeActivity,
    IReadOnlyCollection<OptionChainIvStructureV1> ImpliedVolatilityStructure,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<string> RejectionReasons);