using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Signal.Service;

public static class OptionChainFusionAuditEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainFusionAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/internal/fusion/option-chain/compose", async (
            ThesisFusionRequestV1 request,
            OptionChainFusionRuntimeOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var result = await orchestrator.ComposeAsync(request, cancellationToken);
            var response = new OptionChainFusionAuditResponse(
                request.RequestUid,
                request.InstrumentKey,
                request.AsOfUtc,
                result.Diagnostic.Outcome,
                result.Diagnostic.OutputUid,
                result.Diagnostic.Revision,
                result.Diagnostic.Warnings,
                result.Diagnostic.GateFailures,
                result.Request,
                result.CanContinue);

            return result.CanContinue
                ? Results.Ok(response)
                : Results.UnprocessableEntity(response);
        });

        return endpoints;
    }
}
