using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainFusionEvidenceOptions
{
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromMinutes(7);

    public decimal MinimumConfidence { get; init; } = 0m;

    public void Validate()
    {
        if (MaximumAge <= TimeSpan.Zero || MaximumAge > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        if (MinimumConfidence is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(MinimumConfidence));
    }
}

public sealed record OptionChainFusionEvidenceResult(
    DirectionalEvidenceV1? Evidence,
    IReadOnlyCollection<string> Warnings,
    IReadOnlyCollection<string> GateFailures,
    Guid? OutputUid,
    int? Revision,
    IReadOnlyCollection<Guid> SourceSnapshotUids);

public interface IOptionChainFusionEvidenceProvider
{
    Task<OptionChainFusionEvidenceResult> BuildAsync(
        string instrumentKey,
        DateTimeOffset workflowCutoffUtc,
        CancellationToken cancellationToken = default);
}

public sealed class OptionChainFusionEvidenceProvider : IOptionChainFusionEvidenceProvider
{
    private readonly IOptionChainIntelligenceOutputStore _store;
    private readonly OptionChainFusionEvidenceOptions _options;

    public OptionChainFusionEvidenceProvider(
        IOptionChainIntelligenceOutputStore store,
        OptionChainFusionEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _options = options;
    }

    public async Task<OptionChainFusionEvidenceResult> BuildAsync(
        string instrumentKey,
        DateTimeOffset workflowCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentKey);

        var envelope = await _store.GetLatestAtOrBeforeAsync(
            new OptionChainPointInTimeQuery(instrumentKey, workflowCutoffUtc),
            cancellationToken);
        if (envelope is null)
            return Empty();

        var output = envelope.Output;
        var failures = ValidateHardGates(output, envelope, instrumentKey, workflowCutoffUtc);
        if (failures.Count > 0)
        {
            return new OptionChainFusionEvidenceResult(
                null,
                output.Warnings.ToArray(),
                failures,
                output.OutputUid,
                output.Revision,
                output.SourceSnapshotUids.ToArray());
        }

        var warnings = new HashSet<string>(output.Warnings, StringComparer.Ordinal);
        if (workflowCutoffUtc - output.AsOfUtc > _options.MaximumAge || output.IsStale)
            warnings.Add("OPTION_CHAIN_WORKFLOW_STALE");
        if (!string.Equals(output.DataQualityStatus, "VALID", StringComparison.Ordinal))
            warnings.Add("OPTION_CHAIN_DATA_QUALITY_NOT_VALID");
        if (!output.IsEligibleForFusion || output.Confidence < _options.MinimumConfidence)
            warnings.Add("OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION");

        var direction = ParseDirection(output.Direction);
        var usable = warnings.All(x => x is not
            ("OPTION_CHAIN_WORKFLOW_STALE" or
             "OPTION_CHAIN_DATA_QUALITY_NOT_VALID" or
             "OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION")) &&
            direction != EvidenceDirectionV1.Neutral;

        var evidence = usable
            ? new DirectionalEvidenceV1(
                OptionChainIntelligenceContractV1.FusionEvidenceCode,
                output.EngineVersion,
                OptionChainIntelligenceContractV1.FusionEvidenceCode,
                direction,
                Math.Round(Math.Abs(output.Score) * 100m, 8),
                Math.Round(output.Confidence * 100m, 8),
                output.AsOfUtc,
                BuildReasons(output, envelope, workflowCutoffUtc))
            : null;

        return new OptionChainFusionEvidenceResult(
            evidence,
            warnings.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Array.Empty<string>(),
            output.OutputUid,
            output.Revision,
            output.SourceSnapshotUids.ToArray());
    }

    private static IReadOnlyCollection<string> ValidateHardGates(
        OptionChainIntelligenceOutputV1 output,
        OptionChainPersistenceEnvelope envelope,
        string instrumentKey,
        DateTimeOffset workflowCutoffUtc)
    {
        var failures = new List<string>();
        if (!string.Equals(output.UnderlyingInstrumentKey, instrumentKey, StringComparison.Ordinal))
            failures.Add("OPTION_CHAIN_INSTRUMENT_MISMATCH");
        if (output.AsOfUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_FUTURE_OBSERVATION");
        if (output.GeneratedAtUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_FUTURE_GENERATION");
        if (envelope.SourceReceivedAtUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_FUTURE_SOURCE_RECEIPT");
        if (output.SourceSnapshotUids.Count == 0 || output.SourceSnapshotUids.Any(x => x == Guid.Empty))
            failures.Add("OPTION_CHAIN_LINEAGE_INVALID");
        if (output.SelectionAuthority || output.ExecutionAuthority)
            failures.Add("OPTION_CHAIN_AUTHORITY_MISMATCH");
        if (!string.Equals(output.EngineCode, OptionChainIntelligenceContractV1.EngineCode, StringComparison.Ordinal))
            failures.Add("OPTION_CHAIN_ENGINE_MISMATCH");
        return failures.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyCollection<string> BuildReasons(
        OptionChainIntelligenceOutputV1 output,
        OptionChainPersistenceEnvelope envelope,
        DateTimeOffset workflowCutoffUtc)
    {
        var reasons = new List<string>
        {
            $"OPTION_CHAIN_OUTPUT_UID={output.OutputUid:D}",
            $"OPTION_CHAIN_REVISION={output.Revision}",
            $"OPTION_CHAIN_AS_OF_UTC={output.AsOfUtc:O}",
            $"OPTION_CHAIN_GENERATED_AT_UTC={output.GeneratedAtUtc:O}",
            $"OPTION_CHAIN_SOURCE_RECEIVED_AT_UTC={envelope.SourceReceivedAtUtc:O}",
            $"OPTION_CHAIN_WORKFLOW_CUTOFF_UTC={workflowCutoffUtc:O}",
            $"OPTION_CHAIN_SOURCE_SNAPSHOTS={string.Join(',', output.SourceSnapshotUids.OrderBy(x => x))}",
        };
        reasons.AddRange(output.Evidence.OrderBy(x => x.Code, StringComparer.Ordinal).Select(x =>
            $"{x.Code}:{x.Message}"));
        return reasons;
    }

    private static EvidenceDirectionV1 ParseDirection(string direction) => direction switch
    {
        "LONG" or "STRONG_LONG" => EvidenceDirectionV1.Long,
        "SHORT" or "STRONG_SHORT" => EvidenceDirectionV1.Short,
        _ => EvidenceDirectionV1.Neutral,
    };

    private static OptionChainFusionEvidenceResult Empty() => new(
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        Array.Empty<Guid>());
}
