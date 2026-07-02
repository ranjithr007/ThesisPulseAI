using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public static class SignalDecisionProjectionEndpoints
{
    public static IEndpointRouteBuilder MapSignalDecisionProjectionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/signals/{signalUid:guid}/decision-projection",
            async (
                Guid signalUid,
                ISignalDecisionProjectionStore store,
                CancellationToken cancellationToken) =>
            {
                var projection = await store.GetDecisionProjectionAsync(signalUid, cancellationToken);
                return projection is null ? Results.NotFound() : Results.Ok(projection);
            });
        return endpoints;
    }
}
