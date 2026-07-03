using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainFusionOrchestrationResult(
    ThesisFusionRequestV1? Request,
    OptionChainRuntimeDiagnosticSnapshot Diagnostic,
    bool CanContinue);

public sealed class OptionChainFusionRuntimeOrchestrator(
    OptionChainFusionRequestComposer composer,
    OptionChainFusionRuntimeDiagnostics diagnostics,
    ILogger<OptionChainFusionRuntimeOrchestrator> logger)
{
    public async Task<OptionChainFusionOrchestrationResult> ComposeAsync(
        ThesisFusionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var composition = await composer.ComposeAsync(request, cancellationToken);
        var diagnostic = diagnostics.Snapshot(composition.OptionChain);
        var hardFailure = composition.OptionChain.GateFailures.Count > 0;

        logger.Log(
            hardFailure ? LogLevel.Warning : LogLevel.Information,
            "Option-chain Fusion composition {Outcome} request {RequestUid} instrument {InstrumentKey} cutoff {CutoffUtc} output {OutputUid} revision {Revision} warnings {WarningCount} failures {FailureCount}",
            diagnostic.Outcome,
            request.RequestUid,
            request.InstrumentKey,
            request.AsOfUtc,
            diagnostic.OutputUid,
            diagnostic.Revision,
            diagnostic.Warnings.Count,
            diagnostic.GateFailures.Count);

        return new OptionChainFusionOrchestrationResult(
            hardFailure ? null : composition.Request,
            diagnostic,
            !hardFailure);
    }
}
