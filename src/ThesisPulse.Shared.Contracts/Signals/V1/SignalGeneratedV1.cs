namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string EventType = "signal.generated.v1";

    public static readonly IReadOnlySet<string> Directions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LONG",
            "SHORT",
        };

    public static readonly IReadOnlySet<string> Timeframes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1m",
            "5m",
            "15m",
            "1h",
            "1d",
        };
}

public sealed record SignalEvidenceV1(
    string Code,
    string Message,
    string Impact,
    decimal? Weight);

public sealed record SignalGeneratedV1(
    Guid SignalUid,
    string InstrumentKey,
    string StrategyCode,
    string StrategyVersion,
    string Direction,
    string PrimaryTimeframe,
    IReadOnlyCollection<string> ConfirmationTimeframes,
    decimal Strength,
    decimal Confidence,
    DateTimeOffset EntryOpensAtUtc,
    DateTimeOffset EntryClosesAtUtc,
    decimal ReferencePrice,
    decimal? MinimumPrice,
    decimal? MaximumPrice,
    decimal InvalidationPrice,
    string InvalidationReason,
    int ExpectedHoldingPeriodMinutes,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    string? FusionPolicyVersion,
    IReadOnlyCollection<SignalEvidenceV1> Evidence);
