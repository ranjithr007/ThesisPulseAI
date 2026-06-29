namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalStreamContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string EventType = "signal.summary.changed.v1";
}

public sealed record SignalStreamEventV1(
    Guid EventUid,
    string EventType,
    string ContractVersion,
    Guid SignalUid,
    long? SignalId,
    string InstrumentKey,
    string Direction,
    string PrimaryTimeframe,
    decimal Strength,
    decimal Confidence,
    string Status,
    int StatusSequence,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId);
