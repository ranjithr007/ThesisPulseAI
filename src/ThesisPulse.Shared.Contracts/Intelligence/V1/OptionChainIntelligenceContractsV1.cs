namespace ThesisPulse.Shared.Contracts.Intelligence.V1;

public static class OptionChainIntelligenceContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string EngineCode = "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE";
    public const string EngineVersion = "1.0.0";
    public const string PolicyVersion = "option-chain-intelligence-v1.0.0";
    public const string FusionEvidenceCode = "OPTION_CHAIN";
    public const bool SelectionAuthority = false;
    public const bool ExecutionAuthority = false;
}

public sealed record OptionChainOiWallV1(
    DateOnly ExpiryDate,
    string OptionType,
    string Role,
    decimal StrikePrice,
    decimal OpenInterest,
    decimal SameSideOiShare,
    decimal WallStrength,
    decimal DistanceFraction,
    int Rank);

public sealed record OptionChainOiFlowV1(
    Guid DerivativeContractUid,
    string InstrumentKey,
    DateOnly ExpiryDate,
    string OptionType,
    decimal StrikePrice,
    decimal? PreviousPremium,
    decimal? CurrentPremium,
    decimal? PreviousOpenInterest,
    decimal? CurrentOpenInterest,
    decimal? PremiumChangeFraction,
    decimal? OpenInterestChangeFraction,
    string State,
    decimal NormalizedContribution);

public sealed record OptionChainIvTermPointV1(
    Guid SnapshotUid,
    DateOnly ExpiryDate,
    int DaysToExpiry,
    decimal AtmStrikePrice,
    decimal CallImpliedVolatility,
    decimal PutImpliedVolatility,
    decimal AtmImpliedVolatility,
    string PairMethod);

public sealed record OptionChainEvidenceV1(
    string Code,
    string Message,
    string Impact,
    decimal? RawValue,
    decimal NormalizedValue,
    decimal Weight,
    decimal Contribution,
    decimal Confidence,
    IReadOnlyCollection<string> Warnings);

public sealed record OptionChainMaxPainPointV1(
    decimal SettlementStrike,
    decimal CallPayout,
    decimal PutPayout,
    decimal TotalPayout);

public sealed record OptionChainExpiryMetricsV1(
    Guid SnapshotUid,
    DateOnly ExpiryDate,
    decimal UnderlyingPrice,
    decimal CallOpenInterest,
    decimal PutOpenInterest,
    decimal? PcrOpenInterest,
    decimal CallVolume,
    decimal PutVolume,
    decimal? PcrVolume,
    IReadOnlyCollection<OptionChainOiWallV1> CallWalls,
    IReadOnlyCollection<OptionChainOiWallV1> PutWalls,
    IReadOnlyCollection<OptionChainOiFlowV1> OiFlows,
    decimal? MaxPainStrike,
    decimal? MaxPainDistanceFraction,
    decimal? MaxPainMagnetStrength,
    IReadOnlyCollection<OptionChainMaxPainPointV1> MaxPainCurve,
    decimal? AtmCallImpliedVolatility,
    decimal? AtmPutImpliedVolatility,
    decimal? AtmPutCallSkew,
    decimal? Rr25Skew,
    int AcceptedContractCount,
    int AcceptedStrikeCount,
    decimal ComponentCoverage,
    IReadOnlyCollection<string> Warnings);

public sealed record OptionChainIntelligenceOutputV1(
    Guid OutputUid,
    Guid MessageUid,
    IReadOnlyCollection<Guid> SourceSnapshotUids,
    string UnderlyingInstrumentKey,
    DateTimeOffset AsOfUtc,
    DateTimeOffset GeneratedAtUtc,
    string EngineCode,
    string EngineVersion,
    string PolicyVersion,
    string Direction,
    decimal Score,
    decimal Confidence,
    IReadOnlyCollection<OptionChainExpiryMetricsV1> ExpiryMetrics,
    IReadOnlyCollection<OptionChainIvTermPointV1> IvTermStructure,
    decimal? NearToNextIvSlope,
    decimal? NearToFarIvSlope,
    string IvTermStructureState,
    int InputSnapshotCount,
    int AcceptedContractCount,
    int AcceptedStrikeCount,
    decimal ComponentCoverage,
    string DataQualityStatus,
    bool IsStale,
    bool IsEligibleForFusion,
    int Revision,
    IReadOnlyCollection<OptionChainEvidenceV1> Evidence,
    IReadOnlyCollection<string> Warnings,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed record OptionChainProcessingResultV1(
    string Outcome,
    OptionChainIntelligenceOutputV1? Output,
    string? Reason);
