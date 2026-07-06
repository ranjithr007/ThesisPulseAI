using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ThesisPulse.Shared.Infrastructure.Time;
using ThesisPulse.Shared.Observability.Authentication;

namespace ThesisPulse.Shared.Observability.Auditing;

public static class OperatorAccessAuditExtensions
{
    public static IApplicationBuilder UseThesisPulseOperatorAccessAudit(
        this IApplicationBuilder app)
    {
        app.UseMiddleware<OperatorAccessAuditMiddleware>();
        return app;
    }

    public static IEndpointRouteBuilder MapThesisPulseOperatorAccessAudit(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/security/operator-audit/recent",
                (int? limit,
                 IOperatorAccessAuditStore store,
                 IClock clock) =>
                {
                    var entries = store.GetRecent(limit.GetValueOrDefault(50));
                    return Results.Ok(new OperatorAccessAuditReport(
                        ContractVersion: OperatorAccessAuditContract.ContractVersion,
                        ObservedAtUtc: clock.UtcNow,
                        Count: entries.Count,
                        Entries: entries));
                })
            .RequireAuthorization(OperatorAuthenticationConstants.AdminPolicy);

        return endpoints;
    }
}
