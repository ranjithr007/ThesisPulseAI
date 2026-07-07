using Microsoft.AspNetCore.Routing;

namespace ThesisPulse.Execution.Service;

public static class AdapterReadinessEndpoints
{
    public static IEndpointRouteBuilder MapAdapterReadinessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/adapter/readiness/status",
            (AdapterReadinessOptions options) => Results.Ok(AdapterReadinessStatusBuilder.Build(options)));

        return endpoints;
    }
}
