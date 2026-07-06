using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThesisPulse.Shared.Contracts.Api.V1;
using ThesisPulse.Shared.Infrastructure.Time;
using ThesisPulse.Shared.Observability.Auditing;
using ThesisPulse.Shared.Observability.Authentication;
using ThesisPulse.Shared.Observability.Correlation;

namespace ThesisPulse.Shared.Observability.Hosting;

public static class PlatformFoundationExtensions
{
    public static IServiceCollection AddThesisPulsePlatformFoundation(
        this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new PlatformRuntime(DateTimeOffset.UtcNow));
        services.AddHealthChecks();
        services.AddThesisPulseOperatorAccessAudit();
        services.AddThesisPulseOperatorAuthentication();
        return services;
    }

    public static IServiceCollection AddThesisPulseOperatorAccessAudit(
        this IServiceCollection services)
    {
        services.AddOptions<OperatorAccessAuditOptions>();
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = configuration
                .GetSection(OperatorAccessAuditOptions.SectionName)
                .Get<OperatorAccessAuditOptions>()
                ?? new OperatorAccessAuditOptions();
            options.Validate();
            return options;
        });
        services.AddSingleton<IOperatorAccessAuditStore, InMemoryOperatorAccessAuditStore>();
        return services;
    }

    public static IApplicationBuilder UseThesisPulsePlatformFoundation(
        this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseThesisPulseOperatorAuthentication();
        return app;
    }

    public static IEndpointRouteBuilder MapThesisPulsePlatformEndpoints(
        this IEndpointRouteBuilder endpoints,
        string serviceName,
        string contractVersion = "v1")
    {
        endpoints.MapHealthChecks(
                "/health/live",
                new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready")
            .AllowAnonymous();

        endpoints.MapGet(
                "/health/startup",
                (PlatformRuntime runtime) => Results.Ok(new
                {
                    status = "Healthy",
                    startedAtUtc = runtime.StartedAtUtc,
                }))
            .AllowAnonymous();

        endpoints.MapGet(
                "/info",
                (IHostEnvironment environment,
                 IConfiguration configuration,
                 PlatformRuntime runtime,
                 IClock clock) =>
                {
                    var version = Assembly.GetEntryAssembly()?
                        .GetName()
                        .Version?
                        .ToString() ?? "0.0.0";

                    return Results.Ok(new ServiceInfoResponse(
                        ServiceName: serviceName,
                        ServiceVersion: version,
                        ContractVersion: contractVersion,
                        ConfigurationVersion: configuration["Platform:ConfigurationVersion"] ?? "unversioned",
                        Environment: environment.EnvironmentName,
                        StartedAtUtc: runtime.StartedAtUtc,
                        CurrentTimeUtc: clock.UtcNow));
                })
            .AllowAnonymous();

        endpoints.MapThesisPulseOperatorAccessAudit();

        return endpoints;
    }

    public sealed record PlatformRuntime(DateTimeOffset StartedAtUtc);
}
