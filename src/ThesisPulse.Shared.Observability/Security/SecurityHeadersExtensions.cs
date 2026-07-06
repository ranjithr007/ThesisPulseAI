using Microsoft.AspNetCore.Builder;

namespace ThesisPulse.Shared.Observability.Security;

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseThesisPulseSecurityHeaders(
        this IApplicationBuilder app)
    {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        return app;
    }
}
