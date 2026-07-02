namespace ThesisPulse.Signal.Service;

public sealed class InMemoryOptionChainIntelligenceOutputStore : IOptionChainIntelligenceOutputStore
{
    private readonly object _sync = new();
    private readonly List<OptionChainPersistenceEnvelope> _rows = new();

    public Task<OptionChainAppendResult> AppendAsync(
        OptionChainPersistenceEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Output);

        var rejection = Validate(envelope);
        if (rejection is not null)
        {
            return Task.FromResult(new OptionChainAppendResult(
                OptionChainAppendOutcome.Rejected,
                envelope.Output.OutputUid,
                envelope.Output.Revision,
                rejection));
        }

        lock (_sync)
        {
            var duplicate = _rows.FirstOrDefault(row =>
                row.Output.OutputUid == envelope.Output.OutputUid);
            if (duplicate is not null)
            {
                var samePayload = duplicate == envelope;
                return Task.FromResult(new OptionChainAppendResult(
                    samePayload ? OptionChainAppendOutcome.Duplicate : OptionChainAppendOutcome.Rejected,
                    envelope.Output.OutputUid,
                    envelope.Output.Revision,
                    samePayload ? "OUTPUT_ALREADY_PERSISTED" : "OUTPUT_UID_PAYLOAD_CONFLICT"));
            }

            var sameCutoff = _rows
                .Where(row =>
                    string.Equals(
                        row.Output.UnderlyingInstrumentKey,
                        envelope.Output.UnderlyingInstrumentKey,
                        StringComparison.Ordinal) &&
                    row.Output.AsOfUtc == envelope.Output.AsOfUtc)
                .OrderByDescending(row => row.Output.Revision)
                .FirstOrDefault();

            if (sameCutoff is not null && envelope.Output.Revision <= sameCutoff.Output.Revision)
            {
                return Task.FromResult(new OptionChainAppendResult(
                    OptionChainAppendOutcome.Rejected,
                    envelope.Output.OutputUid,
                    envelope.Output.Revision,
                    "REVISION_NOT_NEWER"));
            }

            _rows.Add(envelope);
            return Task.FromResult(new OptionChainAppendResult(
                OptionChainAppendOutcome.Inserted,
                envelope.Output.OutputUid,
                envelope.Output.Revision,
                null));
        }
    }

    public Task<OptionChainPersistenceEnvelope?> GetLatestAtOrBeforeAsync(
        OptionChainPointInTimeQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.UnderlyingInstrumentKey);

        lock (_sync)
        {
            var result = _rows
                .Where(row =>
                    string.Equals(
                        row.Output.UnderlyingInstrumentKey,
                        query.UnderlyingInstrumentKey,
                        StringComparison.Ordinal) &&
                    row.Output.AsOfUtc <= query.WorkflowCutoffUtc &&
                    row.Output.GeneratedAtUtc <= query.WorkflowCutoffUtc &&
                    row.SourceReceivedAtUtc <= query.WorkflowCutoffUtc)
                .OrderByDescending(row => row.Output.AsOfUtc)
                .ThenByDescending(row => row.Output.Revision)
                .ThenByDescending(row => row.SourceReceivedAtUtc)
                .FirstOrDefault();

            return Task.FromResult(result);
        }
    }

    private static string? Validate(OptionChainPersistenceEnvelope envelope)
    {
        var output = envelope.Output;
        if (output.OutputUid == Guid.Empty) return "OUTPUT_UID_REQUIRED";
        if (string.IsNullOrWhiteSpace(output.UnderlyingInstrumentKey)) return "UNDERLYING_INSTRUMENT_REQUIRED";
        if (output.Revision < 0) return "REVISION_INVALID";
        if (output.GeneratedAtUtc < output.AsOfUtc) return "GENERATED_BEFORE_OBSERVATION";
        if (envelope.SourceReceivedAtUtc < output.AsOfUtc) return "SOURCE_RECEIVED_BEFORE_OBSERVATION";
        if (envelope.PersistedAtUtc < output.GeneratedAtUtc) return "PERSISTED_BEFORE_GENERATION";
        if (output.SelectionAuthority || output.ExecutionAuthority) return "AUTHORITY_DRIFT";
        return null;
    }
}
