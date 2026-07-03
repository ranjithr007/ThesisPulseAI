using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Thesis.Service;

public sealed record OptionChainFusionSourceV1(
    OptionChainIntelligenceOutputV1 Output,
    DateTimeOffset SourceReceivedAtUtc);

public sealed record OptionChainFusionEvidenceResultV1(
    FusionDirectionalEvidenceV1? Evidence,
    IReadOnlyCollection<string> Warnings,
    IReadOnlyCollection<string> Failures)
{
    public bool FailedClosed => Failures.Count > 0;
    public bool Included => Evidence is not null && !FailedClosed;
}

public sealed class OptionChainFusionEvidenceAdapter
{
    private const decimal OneHundred = 100m;

    public OptionChainFusionEvidenceResultV1 Adapt(
        OptionChainFusionSourceV1? source,
        string workflowInstrumentKey,
        DateTimeOffset workflowCutoffUtc,
        int maximumAgeSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstrumentKey);
        if (maximumAgeSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAgeSeconds));

        if (source is null)
        {
            return new OptionChainFusionEvidenceResultV1(
                null,
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        ArgumentNullException.ThrowIfNull(source.Output);
        var output = source.Output;
        var warnings = new HashSet<string>(output.Warnings, StringComparer.Ordinal);
        var failures = new List<string>();

        if (!string.Equals(
                output.UnderlyingInstrumentKey,
                workflowInstrumentKey,
                StringComparison.Ordinal))
        {
            failures.Add("OPTION_CHAIN_INSTRUMENT_LINEAGE_MISMATCH");
        }

        if (output.AsOfUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_OBSERVATION_AFTER_WORKFLOW_CUTOFF");
        if (output.GeneratedAtUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_GENERATION_AFTER_WORKFLOW_CUTOFF");
        if (source.SourceReceivedAtUtc > workflowCutoffUtc)
            failures.Add("OPTION_CHAIN_SOURCE_RECEIPT_AFTER_WORKFLOW_CUTOFF");
        if (output.SelectionAuthority || output.ExecutionAuthority)
            failures.Add("OPTION_CHAIN_AUTHORITY_DRIFT");
        if (output.Score is < -1m or > 1m)
            failures.Add("OPTION_CHAIN_SCORE_OUT_OF_RANGE");
        if (output.Confidence is < 0m or > 1m)
            failures.Add("OPTION_CHAIN_CONFIDENCE_OUT_OF_RANGE");

        if (failures.Count > 0)
        {
            return new OptionChainFusionEvidenceResultV1(
                null,
                warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                failures.Distinct(StringComparer.Ordinal).ToArray());
        }

        var age = workflowCutoffUtc - output.AsOfUtc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(maximumAgeSeconds))
        {
            warnings.Add("OPTION_CHAIN_WORKFLOW_STALE");
            return Excluded(warnings);
        }

        if (!string.Equals(output.DataQualityStatus, "VALID", StringComparison.Ordinal))
        {
            warnings.Add("OPTION_CHAIN_DATA_QUALITY_NOT_VALID");
            return Excluded(warnings);
        }

        if (output.IsStale)
        {
            warnings.Add("OPTION_CHAIN_STALE");
            return Excluded(warnings);
        }

        if (!output.IsEligibleForFusion ||
            output.Direction is not ("LONG" or "SHORT"))
        {
            warnings.Add("OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION");
            return Excluded(warnings);
        }

        var evidence = new FusionDirectionalEvidenceV1(
            output.OutputUid,
            OptionChainIntelligenceContractV1.FusionEvidenceCode,
            output.EngineVersion,
            OptionChainIntelligenceContractV1.FusionEvidenceCode,
            output.Direction,
            decimal.Round(decimal.Abs(output.Score) * OneHundred, 2),
            decimal.Round(output.Confidence * OneHundred, 2),
            output.AsOfUtc,
            output.Evidence
                .Select(item => item.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        return new OptionChainFusionEvidenceResultV1(
            evidence,
            warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Array.Empty<string>());
    }

    private static OptionChainFusionEvidenceResultV1 Excluded(
        HashSet<string> warnings) =>
        new(
            null,
            warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Array.Empty<string>());
}
