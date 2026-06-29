using System.Net;
using System.Net.Http.Json;

namespace ThesisPulse.Operations.Service;

public sealed record PlatformDependencyOptions(
    string Name,
    Uri BaseUrl,
    string ReadinessPath);

public sealed record PlatformHealthOptions
{
    public required IReadOnlyCollection<PlatformDependencyOptions> Dependencies { get; init; }

    public int TimeoutSeconds { get; init; } = 3;
}

public sealed record DependencyHealthSnapshot(
    string Name,
    string Status,
    string BaseUrl,
    string ReadinessPath,
    int? HttpStatus,
    long DurationMilliseconds,
    DateTimeOffset CheckedAtUtc,
    string? Error);

public sealed record PlatformHealthSnapshot(
    string Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyCollection<DependencyHealthSnapshot> Dependencies);

public sealed class PlatformHealthAggregator(
    IHttpClientFactory httpClientFactory,
    PlatformHealthOptions options)
{
    public async Task<PlatformHealthSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = options.Dependencies.Select(dependency =>
            CheckDependencyAsync(dependency, cancellationToken));
        var dependencies = await Task.WhenAll(tasks);
        var overallStatus = dependencies.All(item => item.Status == "HEALTHY")
            ? "HEALTHY"
            : dependencies.Any(item => item.Status == "HEALTHY")
                ? "DEGRADED"
                : "UNHEALTHY";

        return new PlatformHealthSnapshot(
            overallStatus,
            DateTimeOffset.UtcNow,
            dependencies);
    }

    private async Task<DependencyHealthSnapshot> CheckDependencyAsync(
        PlatformDependencyOptions dependency,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var client = httpClientFactory.CreateClient("platform-health");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(dependency.BaseUrl, dependency.ReadinessPath));
            using var response = await client.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            return new DependencyHealthSnapshot(
                dependency.Name,
                response.IsSuccessStatusCode ? "HEALTHY" : "UNHEALTHY",
                dependency.BaseUrl.ToString(),
                dependency.ReadinessPath,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                startedAt,
                response.IsSuccessStatusCode
                    ? null
                    : $"Readiness endpoint returned {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failed(
                dependency,
                startedAt,
                stopwatch.ElapsedMilliseconds,
                "Readiness request timed out.");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return Failed(
                dependency,
                startedAt,
                stopwatch.ElapsedMilliseconds,
                exception.Message);
        }
    }

    private static DependencyHealthSnapshot Failed(
        PlatformDependencyOptions dependency,
        DateTimeOffset checkedAtUtc,
        long durationMilliseconds,
        string error) =>
        new(
            dependency.Name,
            "UNHEALTHY",
            dependency.BaseUrl.ToString(),
            dependency.ReadinessPath,
            HttpStatus: null,
            durationMilliseconds,
            checkedAtUtc,
            error);
}

public static class PlatformHealthAggregationExtensions
{
    public static IServiceCollection AddPlatformHealthAggregation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var timeoutSeconds = configuration.GetValue(
            "PlatformHealth:TimeoutSeconds",
            3);
        if (timeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                "PlatformHealth:TimeoutSeconds must be at least one second.");
        }

        var dependencies = new[]
        {
            CreateDependency(
                "Trading API",
                configuration["PlatformHealth:TradingApiBaseUrl"]
                    ?? "http://localhost:5100"),
            CreateDependency(
                "Signal Service",
                configuration["PlatformHealth:SignalServiceBaseUrl"]
                    ?? "http://localhost:5102"),
        };

        var options = new PlatformHealthOptions
        {
            Dependencies = dependencies,
            TimeoutSeconds = timeoutSeconds,
        };

        services.AddSingleton(options);
        services.AddHttpClient("platform-health", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        services.AddSingleton<PlatformHealthAggregator>();
        return services;
    }

    private static PlatformDependencyOptions CreateDependency(
        string name,
        string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Platform health URL for {name} must be absolute.");
        }

        return new PlatformDependencyOptions(
            name,
            uri,
            "/health/ready");
    }
}
