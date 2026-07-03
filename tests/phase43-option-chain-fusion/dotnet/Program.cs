using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Signal.Service;

var cutoff = new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero);
var snapshotUid = Guid.NewGuid();
var output = CreateOutput(snapshotUid, cutoff.AddMinutes(-2), cutoff.AddMinutes(-1));

var provider = new OptionChainFusionEvidenceProvider(
    new FixedStore(new OptionChainPersistenceEnvelope(output, cutoff.AddSeconds(-30), cutoff.AddSeconds(-10))),
    new OptionChainFusionEvidenceOptions());

var usable = await provider.BuildAsync("NSE:NIFTY50", cutoff);
Assert(usable.Evidence is not null, "usable output should create evidence");
Assert(usable.Evidence!.EngineCode == "OPTION_CHAIN", "engine code");
Assert(usable.Evidence.Timeframe == "OPTION_CHAIN", "timeframe");
Assert(usable.Evidence.Score == 70m, "normalized score");
Assert(usable.Evidence.Confidence == 80m, "normalized confidence");
Assert(usable.Evidence.Reasons.Any(x => x.Contains(output.OutputUid.ToString("D"), StringComparison.Ordinal)), "output lineage");

var staleProvider = new OptionChainFusionEvidenceProvider(
    new FixedStore(new OptionChainPersistenceEnvelope(output with { AsOfUtc = cutoff.AddMinutes(-10) }, cutoff.AddSeconds(-30), cutoff.AddSeconds(-10))),
    new OptionChainFusionEvidenceOptions { MaximumAge = TimeSpan.FromMinutes(5) });
var stale = await staleProvider.BuildAsync("NSE:NIFTY50", cutoff);
Assert(stale.Evidence is null, "stale output must not vote");
Assert(stale.Warnings.Contains("OPTION_CHAIN_WORKFLOW_STALE"), "stale warning");

var authorityProvider = new OptionChainFusionEvidenceProvider(
    new FixedStore(new OptionChainPersistenceEnvelope(output with { ExecutionAuthority = true }, cutoff.AddSeconds(-30), cutoff.AddSeconds(-10))),
    new OptionChainFusionEvidenceOptions());
var authority = await authorityProvider.BuildAsync("NSE:NIFTY50", cutoff);
Assert(authority.Evidence is null, "authority drift must not vote");
Assert(authority.GateFailures.Contains("OPTION_CHAIN_AUTHORITY_MISMATCH"), "authority failure");

var composer = new OptionChainFusionRequestComposer(provider);
var request = CreateRequest(cutoff);
var composed = await composer.ComposeAsync(request with
{
    DirectionalEvidence = request.DirectionalEvidence.Concat(new[]
    {
        new DirectionalEvidenceV1("OPTION_CHAIN", "old", "OPTION_CHAIN", EvidenceDirectionV1.Short, 10m, 10m, cutoff.AddMinutes(-1), Array.Empty<string>()),
    }).ToArray(),
});
Assert(composed.Request.DirectionalEvidence.Count(x => x.EngineCode == "OPTION_CHAIN") == 1, "one option-chain vote");
Assert(composed.EvidenceAdded, "vote added");

Console.WriteLine("PASS: Phase 4.3 option-chain Fusion acceptance");
return 0;

static ThesisFusionRequestV1 CreateRequest(DateTimeOffset cutoff) => new(
    Guid.NewGuid(), Guid.NewGuid().ToString("D"), "NSE:NIFTY50", "5m", cutoff,
    "fusion-weights-v1.0.0",
    new[] { new DirectionalEvidenceV1("TREND", "1.0.0", "5m", EvidenceDirectionV1.Long, 70m, 80m, cutoff.AddMinutes(-1), Array.Empty<string>()) },
    new RegimeEvidenceV1("TRENDING", "1.0.0", EvidenceDirectionV1.Long, 80m, cutoff.AddMinutes(-1), Array.Empty<string>()),
    new[] { new TimeframeConfirmationV1("5m", EvidenceDirectionV1.Long, 70m, 80m, true, cutoff.AddMinutes(-1), Array.Empty<string>()) });

static OptionChainIntelligenceOutputV1 CreateOutput(Guid snapshotUid, DateTimeOffset asOf, DateTimeOffset generated) => new(
    Guid.NewGuid(), Guid.NewGuid(), new[] { snapshotUid }, "NSE:NIFTY50", asOf, generated,
    OptionChainIntelligenceContractV1.EngineCode, OptionChainIntelligenceContractV1.EngineVersion,
    OptionChainIntelligenceContractV1.PolicyVersion, "LONG", 0.70m, 0.80m,
    Array.Empty<OptionChainExpiryMetricsV1>(), Array.Empty<OptionChainIvTermPointV1>(),
    null, null, "FLAT", 1, 0, 0, 1m, "VALID", false, true, 2,
    Array.Empty<OptionChainEvidenceV1>(), Array.Empty<string>(), false, false);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class FixedStore(OptionChainPersistenceEnvelope? envelope) : IOptionChainIntelligenceOutputStore
{
    public Task<OptionChainAppendResult> AppendAsync(OptionChainPersistenceEnvelope value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OptionChainPersistenceEnvelope?> GetLatestAtOrBeforeAsync(OptionChainPointInTimeQuery query, CancellationToken cancellationToken = default) => Task.FromResult(envelope);
}
