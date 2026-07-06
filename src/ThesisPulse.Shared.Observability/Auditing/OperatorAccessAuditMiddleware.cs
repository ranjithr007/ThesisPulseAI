using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThesisPulse.Shared.Infrastructure.Time;
using ThesisPulse.Shared.Observability.Authentication;
using ThesisPulse.Shared.Observability.Correlation;

namespace ThesisPulse.Shared.Observability.Auditing;

public sealed class OperatorAccessAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOperatorAccessAuditStore _store;
    private readonly IClock _clock;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OperatorAccessAuditMiddleware> _logger;

    public OperatorAccessAuditMiddleware(
        RequestDelegate next,
        IOperatorAccessAuditStore store,
        IClock clock,
        IHostEnvironment environment,
        ILogger<OperatorAccessAuditMiddleware> logger)
    {
        _next = next;
        _store = store;
        _clock = clock;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!OperatorAccessAuditClassifier.ShouldAudit(context.Request.Path))
        {
            await _next(context);
            return;
        }

        Exception? failure = null;
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            throw;
        }
        finally
        {
            var entry = CreateEntry(context, failure is not null);
            _store.Record(entry);
            _logger.LogInformation(
                "Operator access audit. ServiceName={ServiceName} Method={Method} Path={Path} StatusCode={StatusCode} CorrelationId={CorrelationId} OperatorSubject={OperatorSubject} RequestClass={RequestClass} AuthorizationOutcome={AuthorizationOutcome}",
                entry.ServiceName,
                entry.Method,
                entry.Path,
                entry.StatusCode,
                entry.CorrelationId,
                entry.OperatorSubject,
                entry.RequestClass,
                entry.AuthorizationOutcome);
        }
    }

    private OperatorAccessAuditEntry CreateEntry(HttpContext context, bool failed)
    {
        var principal = context.User;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;
        var method = context.Request.Method.ToUpperInvariant();
        var path = context.Request.Path.Value ?? "/";
        var statusCode = context.Response.StatusCode;
        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName] as string
            ?? context.TraceIdentifier;

        return new OperatorAccessAuditEntry(
            AuditUid: Guid.NewGuid(),
            ObservedAtUtc: _clock.UtcNow,
            ServiceName: string.IsNullOrWhiteSpace(_environment.ApplicationName)
                ? "ThesisPulse.Unknown.Service"
                : _environment.ApplicationName,
            Method: method,
            Path: path,
            StatusCode: statusCode,
            CorrelationId: correlationId,
            IsAuthenticated: isAuthenticated,
            OperatorSubject: isAuthenticated ? Subject(principal) : "anonymous",
            OperatorName: isAuthenticated ? Name(principal) : "anonymous",
            Permissions: isAuthenticated
                ? OperatorAuthorization.GetPermissions(principal)
                : Array.Empty<string>(),
            RequestClass: OperatorAccessAuditClassifier.Classify(context.Request.Path, method),
            AuthorizationOutcome: OperatorAccessAuditClassifier.Outcome(statusCode, failed));
    }

    private static string Subject(ClaimsPrincipal principal) =>
        principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "unknown";

    private static string Name(ClaimsPrincipal principal) =>
        principal.FindFirst("name")?.Value
        ?? principal.Identity?.Name
        ?? Subject(principal);
}
