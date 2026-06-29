using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalStore : ISignalStore
{
    private readonly ConcurrentDictionary<Guid, StoredSignal> _signals = new();

    public Task<SignalSaveResult> SaveAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);

        var stored = Map(envelope);
        var created = _signals.TryAdd(envelope.Metadata.MessageId, stored);
        var current = created ? stored : _signals[envelope.Metadata.MessageId];

        return Task.FromResult(new SignalSaveResult(
            created ? SignalSaveOutcome.Created : SignalSaveOutcome.Duplicate,
            current.SignalUid,
            current.SignalId));
    }

    public Task<IReadOnlyCollection<StoredSignal>> GetLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        IReadOnlyCollection<StoredSignal> result = _signals.Values
            .OrderByDescending(item => item.GeneratedAtUtc)
            .Take(maximumCount)
            .ToArray();

        return Task.FromResult(result);
    }

    private static StoredSignal Map(EventEnvelope<SignalGeneratedV1> envelope) =>
        new(
            SignalId: null,
            SignalUid: envelope.Payload.SignalUid,
            MessageId: envelope.Metadata.MessageId,
            InstrumentKey: envelope.Payload.InstrumentKey,
            StrategyCode: envelope.Payload.StrategyCode,
            StrategyVersion: envelope.Payload.StrategyVersion,
            Direction: envelope.Payload.Direction,
            PrimaryTimeframe: envelope.Payload.PrimaryTimeframe,
            Strength: envelope.Payload.Strength,
            Confidence: envelope.Payload.Confidence,
            Status: "CANDIDATE",
            GeneratedAtUtc: envelope.Payload.GeneratedAtUtc,
            ValidUntilUtc: envelope.Payload.ValidUntilUtc,
            Producer: envelope.Metadata.Producer,
            CreatorEngineCode: "IN_MEMORY");
}
