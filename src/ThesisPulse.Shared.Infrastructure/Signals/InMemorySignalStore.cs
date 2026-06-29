using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalStore : ISignalStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsByMessage = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsBySignal = new();
    private readonly Dictionary<Guid, SignalTransitionResult> _transitions = new();

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

    public Task<SignalTransitionResult> TransitionStatusAsync(
        Guid signalUid,
        SignalStatusTransitionV1 transition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transition);

        lock (_sync)
        {
            if (_transitions.TryGetValue(transition.TransitionUid, out var duplicate))
            {
                return Task.FromResult(duplicate.SignalUid == signalUid
                    ? duplicate with { Outcome = SignalTransitionOutcome.Duplicate }
                    : Rejected(
                        transition,
                        signalUid,
                        "transitionUid is already assigned to another signal"));
            }

            if (!_signalsBySignal.TryGetValue(signalUid, out var current))
            {
                return Task.FromResult(new SignalTransitionResult(
                    SignalTransitionOutcome.NotFound,
                    transition.TransitionUid,
                    signalUid,
                    SignalId: null,
                    PreviousStatus: null,
                    CurrentStatus: null,
                    EventSequence: null,
                    Reason: "Signal was not found."));
            }

            var validationError = SignalStatusTransitionRules.Validate(
                current.Status,
                transition);

            if (validationError is not null)
            {
                return Task.FromResult(Rejected(
                    transition,
                    signalUid,
                    validationError,
                    current));
            }

            if (transition.RelatedSignalUid.HasValue &&
                (transition.RelatedSignalUid.Value == signalUid ||
                 !_signalsBySignal.ContainsKey(transition.RelatedSignalUid.Value)))
            {
                return Task.FromResult(Rejected(
                    transition,
                    signalUid,
                    "relatedSignalUid must reference a different existing signal",
                    current));
            }

            var updated = current with
            {
                Status = transition.TargetStatus.ToUpperInvariant(),
                StatusSequence = current.StatusSequence + 1,
            };

            _signalsBySignal[signalUid] = updated;
            _signalsByMessage[current.MessageId] = updated;

            var result = new SignalTransitionResult(
                SignalTransitionOutcome.Applied,
                transition.TransitionUid,
                signalUid,
                updated.SignalId,
                current.Status,
                updated.Status,
                updated.StatusSequence,
                Reason: null);

            _transitions.Add(transition.TransitionUid, result);
            return Task.FromResult(result);
        }
    }

    public Task<StoredSignal?> GetAsync(
        Guid signalUid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            _signalsBySignal.TryGetValue(signalUid, out var signal);
            return Task.FromResult(signal);
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

    private static SignalTransitionResult Rejected(
        SignalStatusTransitionV1 transition,
        Guid signalUid,
        string reason,
        StoredSignal? current = null) =>
        new(
            SignalTransitionOutcome.Rejected,
            transition.TransitionUid,
            signalUid,
            current?.SignalId,
            current?.Status,
            current?.Status,
            current?.StatusSequence,
            reason);

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
            Status: SignalStatusV1.Candidate,
            StatusSequence: 0,
            GeneratedAtUtc: envelope.Payload.GeneratedAtUtc,
            ValidUntilUtc: envelope.Payload.ValidUntilUtc,
            Producer: envelope.Metadata.Producer,
            CreatorEngineCode: "IN_MEMORY");
}
