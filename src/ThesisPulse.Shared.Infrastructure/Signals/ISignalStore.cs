using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public enum SignalSaveOutcome
{
    Created = 0,
    Duplicate = 1,
}

public enum SignalTransitionOutcome
{
    Applied = 0,
    Duplicate = 1,
    Rejected = 2,
    NotFound = 3,
}

public sealed record SignalSaveResult(
    SignalSaveOutcome Outcome,
    Guid SignalUid,
    long? SignalId);

public sealed record SignalTransitionResult(
    SignalTransitionOutcome Outcome,
    Guid TransitionUid,
    Guid SignalUid,
    long? SignalId,
    string? PreviousStatus,
    string? CurrentStatus,
    int? EventSequence,
    string? Reason);

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

public interface IFusionSignalStore
{
    Task<SignalSaveResult> SaveFusionAsync(
        FusionSignalIntakeV1 intake,
        CancellationToken cancellationToken = default);
}

public interface ISignalScannerStore
{
    Task<SignalScannerResultV1> ScanAsync(
        SignalScannerQueryV1 query,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public interface ISignalStatusStore
{
    Task<SignalTransitionResult> TransitionStatusAsync(
        Guid signalUid,
        SignalStatusTransitionV1 transition,
        CancellationToken cancellationToken = default);

    Task<StoredSignal?> GetAsync(
        Guid signalUid,
        CancellationToken cancellationToken = default);
}

public interface IDueSignalMaintenanceStore
{
    Task<ExpireDueSignalsResultV1> ExpireDueAsync(
        ExpireDueSignalsRequestV1 request,
        CancellationToken cancellationToken = default);
}
