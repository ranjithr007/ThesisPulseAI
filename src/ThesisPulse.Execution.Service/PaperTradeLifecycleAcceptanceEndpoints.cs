namespace ThesisPulse.Execution.Service;

public static class PaperTradeLifecycleAcceptanceEndpoints
{
    public static IEndpointRouteBuilder MapPaperTradeLifecycleAcceptanceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/execution/lifecycles/{correlationUid:guid}/acceptance",
            async (
                Guid correlationUid,
                string? portfolioCode,
                IPaperTradeLifecycleAcceptanceStore store,
                CancellationToken cancellationToken) =>
            {
                if (!store.IsAvailable)
                {
                    return Results.Problem(
                        title: "PAPER lifecycle acceptance unavailable",
                        detail: store.UnavailableReason,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var report = await store.ReadAsync(
                    correlationUid,
                    string.IsNullOrWhiteSpace(portfolioCode) ? null : portfolioCode.Trim(),
                    cancellationToken);
                return report is null ? Results.NotFound() : Results.Ok(report);
            });

        return endpoints;
    }
}
