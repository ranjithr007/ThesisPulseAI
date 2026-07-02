using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainPersistenceEnvelope(
    OptionChainIntelligenceOutputV1 Output,
    DateTimeOffset SourceReceivedAtUtc,
    DateTimeOffset PersistedAtUtc);

public sealed record OptionChainPointInTimeQuery(
    string UnderlyingInstrumentKey,
    DateTimeOffset WorkflowCutoffUtc);

public enum OptionChainAppendOutcome
{
    Inserted = 1,
    Duplicate = 2,
    Rejected = 3,
}

public sealed record OptionChainAppendResult(
    OptionChainAppendOutcome Outcome,
    Guid OutputUid,
    int Revision,
    string? Reason);

public interface IOptionChainIntelligenceOutputStore
{
    Task<OptionChainAppendResult> AppendAsync(
        OptionChainPersistenceEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<OptionChainPersistenceEnvelope?> GetLatestAtOrBeforeAsync(
        OptionChainPointInTimeQuery query,
        CancellationToken cancellationToken = default);
}
