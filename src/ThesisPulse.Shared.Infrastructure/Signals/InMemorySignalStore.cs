using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalStore :
    ISignalStore,
    ISignalStatusStore,
    IDueSignalMaintenanceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsByMessage = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsBySignal = new();
    private readonly Dictionary<Guid, SignalTransitionResult> _transitions = new();
    private readonly Dictionary<Guid, int> _statusSequences = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastStatusTimes = new();

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
            _statusSequences.Add(stored.SignalUid, 0);
            _lastStatusTimes.Add(stored.SignalUid, envelope.Payload.GeneratedAtUtc);

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
            return Task.FromResult(TransitionStatusCore(signalUid, transition));
        }
    }

    public Task<ExpireDueSignalsResultV1> ExpireDueAsync(
        ExpireDueSignalsRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExpiryRequest(request);

        lock (_sync)
        {
            var dueSignals = _signalsBySignal.Values
                .Where(signal =>
                    signal.ValidUntilUtc <= request.AsOfUtc &&
                    (signal.Status.Equals(
                         SignalStatusV1.Candidate,
                         StringComparison.OrdinalIgnoreCase) ||
                     signal.Status.Equals(
                         SignalStatusV1.Validated,
                         StringComparison.OrdinalIgnoreCase)))
                .OrderBy(signal => signal.ValidUntilUtc)
                .Take(request.MaximumCount)
                .ToArray();

            var expired = new List<ExpiredSignalV1>(dueSignals.Length);

            foreach (var signal in dueSignals)
            {
                var transition = new SignalStatusTransitionV1(
                    TransitionUid: Guid.NewGuid(),
                    TargetStatus: SignalStatusV1.Expired,
                    ReasonCodes: new[] { "VALIDITY_WINDOW_ELAPSED" },
                    OccurredAtUtc: request.AsOfUtc,
                    SourceService: request.SourceService,
                    SourceVersion: request.SourceVersion,
                    CorrelationId: request.CorrelationId,
                    CausationId: null,
                    RelatedSignalUid: null,
                    Metadata: new Dictionary<string, string>
                    {
                        ["job"] = "signal-expiry",
                    });

                var result = TransitionStatusCore(signal.SignalUid, transition);
                if (result.Outcome != SignalTransitionOutcome.Applied)
                {
                    continue;
                }

                expired.Add(new ExpiredSignalV1(
                    result.TransitionUid,
                    result.SignalUid,
                    result.SignalId,
                    result.PreviousStatus!,
                    result.CurrentStatus!,
                    result.EventSequence!.Value));
            }

            return Task.FromResult(new ExpireDueSignalsResultV1(
                request.AsOfUtc,
                dueSignals.Length,
                expired.Count,
                expired));
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

    private SignalTransitionResult TransitionStatusCore(
        Guid signalUid,
        SignalStatusTransitionV1 transition)
    {
        if (_transitions.TryGetValue(transition.TransitionUid, out var duplicate))
        {
            return duplicate.SignalUid == signalUid
                ? duplicate with { Outcome = SignalTransitionOutcome.Duplicate }
                : Rejected(
                    transition,
                    signalUid,
                    "transitionUid is already assigned to another signal");
        }

        if (!_signalsBySignal.TryGetValue(signalUid, out var current))
        {
            return new SignalTransitionResult(
                SignalTransitionOutcome.NotFound,
                transition.TransitionUid,
                signalUid,
                SignalId: null,
                PreviousStatus: null,
                CurrentStatus: null,
                EventSequence: null,
                Reason: "Signal was not found.");
        }

        var validationError = SignalStatusTransitionRules.Validate(
            current.Status,
            transition);

        if (validationError is not null)
        {
            return Rejected(transition, signalUid, validationError, current);
        }

        if (transition.OccurredAtUtc < _lastStatusTimes[signalUid])
        {
            return Rejected(
                transition,
                signalUid,
                "occurredAtUtc cannot be earlier than the latest status event",
                current);
        }

        if (transition.RelatedSignalUid.HasValue &&
            (transition.RelatedSignalUid.Value == signalUid ||
             !_signalsBySignal.ContainsKey(transition.RelatedSignalUid.Value)))
        {
            return Rejected(
                transition,
                signalUid,
                "relatedSignalUid must reference a different existing signal",
                current);
        }

        var nextSequence = _statusSequences[signalUid] + 1;
        var updated = current with
        {
            Status = transition.TargetStatus.ToUpperInvariant(),
        };

        _signalsBySignal[signalUid] = updated;
        _signalsByMessage[current.MessageId] = updated;
        _statusSequences[signalUid] = nextSequence;
        _lastStatusTimes[signalUid] = transition.OccurredAtUtc;

        var result = new SignalTransitionResult(
            SignalTransitionOutcome.Applied,
            transition.TransitionUid,
            signalUid,
            updated.SignalId,
            current.Status,
            updated.Status,
            nextSequence,
            Reason: null);

        _transitions.Add(transition.TransitionUid, result);
        return result;
    }

    private static void ValidateExpiryRequest(ExpireDueSignalsRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AsOfUtc == default)
        {
            throw new ArgumentException("asOfUtc is required", nameof(request));
        }

        if (request.MaximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCount));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceService);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
    }

    private SignalTransitionResult Rejected(
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
            current is null ? null : _statusSequences[current.SignalUid],
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
            GeneratedAtUtc: envelope.Payload.GeneratedAtUtc,
            ValidUntilUtc: envelope.Payload.ValidUntilUtc,
            Producer: envelope.Metadata.Producer,
            CreatorEngineCode: "IN_MEMORY");
}
