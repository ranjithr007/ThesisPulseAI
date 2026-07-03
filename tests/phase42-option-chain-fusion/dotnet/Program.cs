using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Thesis.Service;

var tests = new (string Name, Action Run)[]
{
    ("valid output produces one option-chain vote", ValidOutputProducesVote),
    ("missing output produces no vote", MissingOutputProducesNoVote),
    ("neutral output is warning-only", NeutralOutputIsWarningOnly),
    ("stale output is warning-only", StaleOutputIsWarningOnly),
    ("invalid quality is warning-only", InvalidQualityIsWarningOnly),
    ("future generation fails closed", FutureGenerationFailsClosed),
    ("future source receipt fails closed", FutureSourceReceiptFailsClosed),
    ("instrument mismatch fails closed", InstrumentMismatchFailsClosed),
    ("authority drift fails closed", AuthorityDriftFailsClosed),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ValidOutputProducesVote()
{
    var adapter = new OptionChainFusionEvidenceAdapter();
    var source = Source(Output());
    var result = adapter.Adapt(source, "NSE:NIFTY50", Cutoff(), 120);

    True(result.Included, "evidence must be included");
    True(!result.FailedClosed, "valid output must not fail closed");
    True(result.Evidence is not null, "vote required");
    Equal("OPTION_CHAIN", result.Evidence!.EngineCode, "engine code");
    Equal("OPTION_CHAIN", result.Evidence.Timeframe, "timeframe");
    Equal("LONG", result.Evidence.Direction, "direction");
    Equal(70m, result.Evidence.Score, "score");
    Equal(80m, result.Evidence.Confidence, "confidence");
    Equal(1, result.Evidence.Reasons.Count, "reason count");
}

static void MissingOutputProducesNoVote()
{
    var result = new OptionChainFusionEvidenceAdapter().Adapt(
        null,
        "NSE:NIFTY50",
        Cutoff(),
        120);

    True(!result.Included, "missing output cannot be included");
    True(!result.FailedClosed, "missing optional output is not a hard failure");
    Equal(0, result.Warnings.Count, "warning count");
}

static void NeutralOutputIsWarningOnly()
{
    var output = Output() with { Direction = "NEUTRAL", IsEligibleForFusion = false };
    var result = Adapt(output);

    WarningOnly(result, "OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION");
}

static void StaleOutputIsWarningOnly()
{
    var output = Output() with { IsStale = true };
    var result = Adapt(output);

    WarningOnly(result, "OPTION_CHAIN_STALE");
}

static void InvalidQualityIsWarningOnly()
{
    var output = Output() with { DataQualityStatus = "DEGRADED" };
    var result = Adapt(output);

    WarningOnly(result, "OPTION_CHAIN_DATA_QUALITY_NOT_VALID");
}

static void FutureGenerationFailsClosed()
{
    var output = Output() with { GeneratedAtUtc = Cutoff().AddSeconds(1) };
    var result = Adapt(output);

    Failure(result, "OPTION_CHAIN_GENERATION_AFTER_WORKFLOW_CUTOFF");
}

static void FutureSourceReceiptFailsClosed()
{
    var source = Source(Output()) with { SourceReceivedAtUtc = Cutoff().AddSeconds(1) };
    var result = new OptionChainFusionEvidenceAdapter().Adapt(
        source,
        "NSE:NIFTY50",
        Cutoff(),
        120);

    Failure(result, "OPTION_CHAIN_SOURCE_RECEIPT_AFTER_WORKFLOW_CUTOFF");
}

static void InstrumentMismatchFailsClosed()
{
    var result = new OptionChainFusionEvidenceAdapter().Adapt(
        Source(Output()),
        "NSE:BANKNIFTY",
        Cutoff(),
        120);

    Failure(result, "OPTION_CHAIN_INSTRUMENT_LINEAGE_MISMATCH");
}

static void AuthorityDriftFailsClosed()
{
    var output = Output() with { ExecutionAuthority = true };
    var result = Adapt(output);

    Failure(result, "OPTION_CHAIN_AUTHORITY_DRIFT");
}

static OptionChainFusionEvidenceResultV1 Adapt(OptionChainIntelligenceOutputV1 output) =>
    new OptionChainFusionEvidenceAdapter().Adapt(
        Source(output),
        "NSE:NIFTY50",
        Cutoff(),
        120);

static OptionChainFusionSourceV1 Source(OptionChainIntelligenceOutputV1 output) =>
    new(output, output.AsOfUtc.AddSeconds(2));

static DateTimeOffset Cutoff() =>
    new(2026, 7, 3, 9, 31, 0, TimeSpan.Zero);

static OptionChainIntelligenceOutputV1 Output() =>
    new(
        Guid.Parse("70000000-0000-0000-0000-000000000001"),
        Guid.Parse("70000000-0000-0000-0000-000000000002"),
        new[] { Guid.Parse("70000000-0000-0000-0000-000000000003") },
        "NSE:NIFTY50",
        new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 3, 9, 30, 2, TimeSpan.Zero),
        OptionChainIntelligenceContractV1.EngineCode,
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion,
        "LONG",
        0.70m,
        0.80m,
        Array.Empty<OptionChainExpiryMetricsV1>(),
        Array.Empty<OptionChainIvTermPointV1>(),
        null,
        null,
        "FLAT",
        1,
        0,
        0,
        1m,
        "VALID",
        false,
        true,
        0,
        new[]
        {
            new OptionChainEvidenceV1(
                "PCR_OI",
                "PCR by open interest supports LONG",
                "BULLISH",
                1.2m,
                0.7m,
                0.2m,
                0.14m,
                0.8m,
                Array.Empty<string>()),
        },
        Array.Empty<string>(),
        false,
        false);

static void WarningOnly(
    OptionChainFusionEvidenceResultV1 result,
    string expectedWarning)
{
    True(!result.Included, "warning-only result cannot be included");
    True(!result.FailedClosed, "warning-only result cannot fail closed");
    True(result.Evidence is null, "warning-only result cannot contain evidence");
    True(result.Warnings.Contains(expectedWarning), $"missing warning {expectedWarning}");
}

static void Failure(
    OptionChainFusionEvidenceResultV1 result,
    string expectedFailure)
{
    True(result.FailedClosed, "hard violation must fail closed");
    True(!result.Included, "failed result cannot be included");
    True(result.Evidence is null, "failed result cannot contain evidence");
    True(result.Failures.Contains(expectedFailure), $"missing failure {expectedFailure}");
}

static void True(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
