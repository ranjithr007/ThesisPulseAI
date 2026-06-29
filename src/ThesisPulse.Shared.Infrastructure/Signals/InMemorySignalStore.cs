using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalStore : ISignalStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsByMessage = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsBySignal = new();

    public Task<SignalSaveResult> SaveAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_sync)
        {
            if (_signalsByMessage.TryGetValue(
                    envelope.Metadata.MessageId,
                    out var messageDuplicate))
            {
                return Task.FromResult(ToDuplicate(messageDuplicate));
            }

            if (_signalsBySignal.TryGetValue(
                    envelope.Payload.SignalUid,
                    out var signalDuplicate))
            {
                return Task.FromResult(ToDuplicate(signalDuplicate));
            }

            var stored = Map(envelope);
            _signalsByMessage.Add(stored.MessageId, stored);
            _signalsBySignal.Add(stored.SignalUid, stored);

            return Task.FromResult(new SignalSaveResult(
                SignalSaveOutcome.Created,
                stored.SignalUid,
                stored.SignalId));
        }
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

        lock (_sync)
        {
            IReadOnlyCollection<StoredSignal> result = _signalsByMessage.Values
                .OrderByDescending(item => item.GeneratedAtUtc)
                .Take(maximumCount)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    private static SignalSaveResult ToDuplicate(StoredSignal signal) =>
        new(SignalSaveOutcome.Duplicate, signal.SignalUid, signal.SignalId);

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
