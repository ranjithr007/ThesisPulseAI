using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalStore :
    ISignalStore,
    IFusionSignalStore,
    ISignalScannerStore,
    ISignalStatusStore,
    IDueSignalMaintenanceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsByMessage = new();
    private readonly Dictionary<Guid, StoredSignal> _signalsBySignal = new();
    private readonly Dictionary<Guid, FusionSignalLineageV1> _lineageBySignal = new();
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
            return Task.FromResult(SaveCore(envelope, lineage: null));
        }
    }

    public Task<SignalSaveResult> SaveFusionAsync(
        FusionSignalIntakeV1 intake,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intake);
        ValidateFusionLineage(intake);

        lock (_sync)
        {
            return Task.FromResult(SaveCore(intake.Envelope, intake.Lineage));
        }
    }

    public Task<SignalScannerResultV1> ScanAsync(
        SignalScannerQueryV1 query,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScannerQuery(query, asOfUtc);

        lock (_sync)
        {
            var rows = _signalsBySignal.Values
                .Where(signal => Matches(signal, query, asOfUtc))
                .OrderByDescending(signal => signal.GeneratedAtUtc)
                .ThenByDescending(signal => signal.SignalUid)
                .Take(query.MaximumCount)
                .Select(signal => ToScannerRow(signal, asOfUtc))
                .ToArray();
            return Task.FromResult(new SignalScannerResultV1(asOfUtc, rows, rows.Length));
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
                    IsOpenStatus(signal.Status))
                .OrderBy(signal => signal.ValidUntilUtc)
                .Take(request.MaximumCount)
                .ToArray();
            var expired = new List<ExpiredSignalV1>(dueSignals.Length);

            foreach (var signal in dueSignals)
            {
                var transition = new SignalStatusTransitionV1(
                    Guid.NewGuid(),
                    SignalStatusV1.Expired,
                    new[] { "VALIDITY_WINDOW_ELAPSED" },
                    request.AsOfUtc,
                    request.SourceService,
                    request.SourceVersion,
                    request.CorrelationId,
                    null,
                    null,
                    new Dictionary<string, string> { ["job"] = "signal-expiry" });
                var result = TransitionStatusCore(signal.SignalUid, transition);
                if (result.Outcome != SignalTransitionOutcome.Applied)
                    continue;
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
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        lock (_sync)
        {
            IReadOnlyCollection<StoredSignal> result = _signalsByMessage.Values
                .OrderByDescending(item => item.GeneratedAtUtc)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private SignalSaveResult SaveCore(
        EventEnvelope<SignalGeneratedV1> envelope,
        FusionSignalLineageV1? lineage)
    {
        if (_signalsByMessage.TryGetValue(envelope.Metadata.MessageId, out var messageDuplicate))
        {
            EnsureDuplicateLineage(messageDuplicate.SignalUid, lineage);
            return ToDuplicate(messageDuplicate);
        }
        if (_signalsBySignal.TryGetValue(envelope.Payload.SignalUid, out var signalDuplicate))
        {
            EnsureDuplicateLineage(signalDuplicate.SignalUid, lineage);
            return ToDuplicate(signalDuplicate);
        }

        var stored = Map(envelope);
        _signalsByMessage.Add(stored.MessageId, stored);
        _signalsBySignal.Add(stored.SignalUid, stored);
        _statusSequences.Add(stored.SignalUid, 0);
        _lastStatusTimes.Add(stored.SignalUid, envelope.Payload.GeneratedAtUtc);
        if (lineage is not null)
            _lineageBySignal.Add(stored.SignalUid, lineage);

        return new SignalSaveResult(SignalSaveOutcome.Created, stored.SignalUid, stored.SignalId);
    }

    private void EnsureDuplicateLineage(Guid signalUid, FusionSignalLineageV1? lineage)
    {
        if (lineage is null)
            return;
        if (!_lineageBySignal.TryGetValue(signalUid, out var existing) || existing != lineage)
            throw new InvalidOperationException("Duplicate signal has different Fusion lineage.");
    }

    private SignalScannerRowV1 ToScannerRow(StoredSignal signal, DateTimeOffset asOfUtc)
    {
        _lineageBySignal.TryGetValue(signal.SignalUid, out var lineage);
        return new SignalScannerRowV1(
            signal.SignalUid,
            signal.MessageId,
            signal.InstrumentKey,
            signal.StrategyCode,
            signal.StrategyVersion,
            signal.Direction,
            signal.PrimaryTimeframe,
            signal.Strength,
            signal.Confidence,
            signal.Status,
            signal.GeneratedAtUtc,
            signal.ValidUntilUtc,
            IsOpenStatus(signal.Status) && signal.ValidUntilUtc > asOfUtc,
            signal.Producer,
            signal.CreatorEngineCode,
            lineage?.ThesisUid,
            lineage?.ThesisRequestUid,
            lineage?.FusionEvidenceUid,
            lineage?.SourceCandleMessageUid,
            lineage?.ConfirmationOutputUid,
            lineage?.ConfirmationMessageUid,
            lineage?.FusionEngineVersion,
            lineage?.FusionPolicyVersion,
            lineage?.WeightConfigurationVersion,
            SignalScannerContractV1.RiskNotEvaluated,
            null,
            null);
    }

    private static bool Matches(
        StoredSignal signal,
        SignalScannerQueryV1 query,
        DateTimeOffset asOfUtc)
    {
        if (!string.IsNullOrWhiteSpace(query.InstrumentKey) &&
            !string.Equals(signal.InstrumentKey, query.InstrumentKey.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Direction) &&
            !string.Equals(signal.Direction, query.Direction.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !string.Equals(signal.Status, query.Status.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.MinimumConfidence.HasValue && signal.Confidence < query.MinimumConfidence.Value)
            return false;
        if (query.GeneratedFromUtc.HasValue && signal.GeneratedAtUtc < query.GeneratedFromUtc.Value)
            return false;
        if (query.GeneratedToUtc.HasValue && signal.GeneratedAtUtc > query.GeneratedToUtc.Value)
            return false;
        if (query.ActiveOnly && (!IsOpenStatus(signal.Status) || signal.ValidUntilUtc <= asOfUtc))
            return false;
        return true;
    }

    private SignalTransitionResult TransitionStatusCore(
        Guid signalUid,
        SignalStatusTransitionV1 transition)
    {
        if (_transitions.TryGetValue(transition.TransitionUid, out var duplicate))
        {
            return duplicate.SignalUid == signalUid
                ? duplicate with { Outcome = SignalTransitionOutcome.Duplicate }
                : Rejected(transition, signalUid, "transitionUid is already assigned to another signal");
        }
        if (!_signalsBySignal.TryGetValue(signalUid, out var current))
        {
            return new SignalTransitionResult(
                SignalTransitionOutcome.NotFound,
                transition.TransitionUid,
                signalUid,
                null,
                null,
                null,
                null,
                "Signal was not found.");
        }

        var validationError = SignalStatusTransitionRules.Validate(current.Status, transition);
        if (validationError is not null)
            return Rejected(transition, signalUid, validationError, current);
        if (transition.OccurredAtUtc < _lastStatusTimes[signalUid])
            return Rejected(
                transition,
                signalUid,
                "occurredAtUtc cannot be earlier than the latest status event",
                current);
        if (transition.RelatedSignalUid.HasValue &&
            (transition.RelatedSignalUid.Value == signalUid ||
             !_signalsBySignal.ContainsKey(transition.RelatedSignalUid.Value)))
            return Rejected(
                transition,
                signalUid,
                "relatedSignalUid must reference a different existing signal",
                current);

        var nextSequence = _statusSequences[signalUid] + 1;
        var updated = current with { Status = transition.TargetStatus.ToUpperInvariant() };
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
            null);
        _transitions.Add(transition.TransitionUid, result);
        return result;
    }

    private static void ValidateFusionLineage(FusionSignalIntakeV1 intake)
    {
        var lineage = intake.Lineage;
        var envelope = intake.Envelope;
        if (lineage.ThesisUid == Guid.Empty || lineage.ThesisRequestUid == Guid.Empty ||
            lineage.CandidateSignalUid == Guid.Empty || lineage.FusionEvidenceUid == Guid.Empty ||
            lineage.SourceCandleMessageUid == Guid.Empty || lineage.ConfirmationOutputUid == Guid.Empty ||
            lineage.ConfirmationMessageUid == Guid.Empty)
            throw new ArgumentException("Fusion signal lineage is incomplete.", nameof(intake));
        if (lineage.CandidateSignalUid != envelope.Payload.SignalUid)
            throw new ArgumentException("Candidate signal lineage does not match the signal payload.", nameof(intake));
        if (!string.Equals(
                envelope.Metadata.CausationId,
                lineage.FusionEvidenceUid.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fusion evidence causation does not match lineage.", nameof(intake));
    }

    private static void ValidateScannerQuery(SignalScannerQueryV1 query, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (asOfUtc == default)
            throw new ArgumentException("asOfUtc is required", nameof(asOfUtc));
        if (query.MaximumCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query.MaximumCount));
        if (query.MinimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(query.MinimumConfidence));
        if (query.GeneratedFromUtc.HasValue && query.GeneratedToUtc.HasValue &&
            query.GeneratedFromUtc > query.GeneratedToUtc)
            throw new ArgumentException("generatedFromUtc cannot be after generatedToUtc", nameof(query));
        if (!string.IsNullOrWhiteSpace(query.Direction) &&
            !SignalContractV1.Directions.Contains(query.Direction.Trim()))
            throw new ArgumentException("direction is not supported", nameof(query));
        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !SignalStatusV1.Values.Contains(query.Status.Trim()))
            throw new ArgumentException("status is not supported", nameof(query));
    }

    private static void ValidateExpiryRequest(ExpireDueSignalsRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AsOfUtc == default)
            throw new ArgumentException("asOfUtc is required", nameof(request));
        if (request.MaximumCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCount));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceService);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
    }

    private static bool IsOpenStatus(string status) =>
        status.Equals(SignalStatusV1.Candidate, StringComparison.OrdinalIgnoreCase) ||
        status.Equals(SignalStatusV1.Validated, StringComparison.OrdinalIgnoreCase);

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
            null,
            envelope.Payload.SignalUid,
            envelope.Metadata.MessageId,
            envelope.Payload.InstrumentKey,
            envelope.Payload.StrategyCode,
            envelope.Payload.StrategyVersion,
            envelope.Payload.Direction,
            envelope.Payload.PrimaryTimeframe,
            envelope.Payload.Strength,
            envelope.Payload.Confidence,
            SignalStatusV1.Candidate,
            envelope.Payload.GeneratedAtUtc,
            envelope.Payload.ValidUntilUtc,
            envelope.Metadata.Producer,
            "IN_MEMORY");
}
