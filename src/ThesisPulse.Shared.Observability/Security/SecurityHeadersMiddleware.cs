using Microsoft.AspNetCore.Http;

namespace ThesisPulse.Shared.Observability.Security;

public sealed class SecurityHeadersMiddleware
{
    public const string ContentTypeOptions = "X-Content-Type-Options";
    public const string ReferrerPolicy = "Referrer-Policy";
    public const string FrameOptions = "X-Frame-Options";
    public const string CrossOriginOpenerPolicy = "Cross-Origin-Opener-Policy";
    public const string CrossOriginResourcePolicy = "Cross-Origin-Resource-Policy";
    public const string PermissionsPolicy = "Permissions-Policy";
    public const string StrictTransportSecurity = "Strict-Transport-Security";

    public const string PermissionsPolicyValue =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), fullscreen=(), interest-cohort=()";

    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            Apply(context.Response.Headers, context.Request.IsHttps);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    public void Apply(IHeaderDictionary headers, bool requestIsHttps)
    {
        headers[ContentTypeOptions] = "nosniff";
        headers[ReferrerPolicy] = "no-referrer";
        headers[FrameOptions] = "DENY";
        headers[CrossOriginOpenerPolicy] = "same-origin";
        headers[CrossOriginResourcePolicy] = "same-site";
        headers[PermissionsPolicy] = PermissionsPolicyValue;

        if (_options.EnableStrictTransportSecurity && requestIsHttps)
        {
            headers[StrictTransportSecurity] = _options.BuildStrictTransportSecurityHeader();
        }
    }
}
