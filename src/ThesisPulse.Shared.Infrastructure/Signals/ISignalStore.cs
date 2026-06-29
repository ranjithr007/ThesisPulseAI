using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public enum SignalSaveOutcome
{
    Created = 0,
    Duplicate = 1,
}

public sealed record SignalSaveResult(
    SignalSaveOutcome Outcome,
    Guid SignalUid,
    long? SignalId);

public sealed record StoredSignal(
    long? SignalId,
    Guid SignalUid,
    Guid MessageId,
    string InstrumentKey,
    string StrategyCode,
    string StrategyVersion,
    string Direction,
    string PrimaryTimeframe,
    decimal Strength,
    decimal Confidence,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    string Producer,
    string CreatorEngineCode);

public interface ISignalStore
{
    Task<SignalSaveResult> SaveAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StoredSignal>> GetLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}
