using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThesisPulse.Shared.Contracts.Api.V1;
using ThesisPulse.Shared.Infrastructure.Time;
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
        return services;
    }

    public static IApplicationBuilder UseThesisPulsePlatformFoundation(
        this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();

    public static IEndpointRouteBuilder MapThesisPulsePlatformEndpoints(
        this IEndpointRouteBuilder endpoints,
        string serviceName,
        string contractVersion = "v1")
    {
        endpoints.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions { Predicate = _ => false });

        endpoints.MapHealthChecks("/health/ready");

        endpoints.MapGet(
            "/health/startup",
            (PlatformRuntime runtime) => Results.Ok(new
            {
                status = "Healthy",
                startedAtUtc = runtime.StartedAtUtc,
            }));

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
            });

        return endpoints;
    }

    public sealed record PlatformRuntime(DateTimeOffset StartedAtUtc);
}
