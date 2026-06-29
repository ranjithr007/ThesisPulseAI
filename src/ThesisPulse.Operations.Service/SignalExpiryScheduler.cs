using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Operations.Service;

public sealed record SignalExpiryOptions
{
    public bool Enabled { get; init; }

    public required Uri SignalServiceBaseUrl { get; init; }

    public string? InternalApiKey { get; init; }

    public int IntervalSeconds { get; init; } = 30;

    public int BatchSize { get; init; } = 100;

    public int TimeoutSeconds { get; init; } = 10;
}

public sealed record SignalExpiryRunSnapshot(
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Selected,
    int Expired,
    int Published,
    int PublicationFailures,
    string? CorrelationId,
    string? Error);

public sealed class SignalExpiryJobState
{
    private readonly object _sync = new();
    private SignalExpiryRunSnapshot _snapshot = new(
        Status: "NOT_RUN",
        StartedAtUtc: null,
        CompletedAtUtc: null,
        Selected: 0,
        Expired: 0,
        Published: 0,
        PublicationFailures: 0,
        CorrelationId: null,
        Error: null);

    public SignalExpiryRunSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public void Started(DateTimeOffset startedAtUtc, string correlationId)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "RUNNING",
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = null,
                CorrelationId = correlationId,
                Error = null,
            };
        }
    }

    public void Completed(
        DateTimeOffset completedAtUtc,
        SignalExpiryResponse response)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "COMPLETED",
                CompletedAtUtc = completedAtUtc,
                Selected = response.Selected,
                Expired = response.Expired,
                Published = response.Published,
                PublicationFailures = response.PublicationFailures,
                Error = null,
            };
        }
    }

    public void Failed(DateTimeOffset completedAtUtc, string error)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "FAILED",
                CompletedAtUtc = completedAtUtc,
                Error = error,
            };
        }
    }
}

public sealed record SignalExpiryResponse(
    string Status,
    DateTimeOffset AsOfUtc,
    int Selected,
    int Expired,
    int Published,
    int PublicationFailures,
    IReadOnlyCollection<ExpiredSignalV1> Signals);

public sealed class SignalExpiryClient(
    HttpClient httpClient,
    SignalExpiryOptions options)
{
    public async Task<SignalExpiryResponse> ExpireDueAsync(
        DateTimeOffset asOfUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var requestBody = new ExpireDueSignalsRequestV1(
            AsOfUtc: asOfUtc,
            MaximumCount: options.BatchSize,
            SourceService: "ThesisPulse.Operations.Service",
            SourceVersion: "0.1.0",
            CorrelationId: correlationId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/v1/signals/expire-due")
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.Add("X-ThesisPulse-Internal-Key", options.InternalApiKey);
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SignalExpiryResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Signal Service returned an empty expiry response.");
    }
}

public sealed class SignalExpiryWorker(
    SignalExpiryOptions options,
    SignalExpiryClient client,
    SignalExpiryJobState state,
    ILogger<SignalExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Signal expiry scheduler is disabled.");
            return;
        }

        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.IntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid().ToString("D");
        state.Started(startedAtUtc, correlationId);

        try
        {
            var response = await client.ExpireDueAsync(
                startedAtUtc,
                correlationId,
                cancellationToken);
            state.Completed(DateTimeOffset.UtcNow, response);

            logger.LogInformation(
                "Signal expiry run completed. Selected={Selected}, Expired={Expired}, " +
                "Published={Published}, PublicationFailures={PublicationFailures}, " +
                "CorrelationId={CorrelationId}",
                response.Selected,
                response.Expired,
                response.Published,
                response.PublicationFailures,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.Failed(DateTimeOffset.UtcNow, exception.Message);
            logger.LogError(
                exception,
                "Signal expiry run failed. CorrelationId={CorrelationId}",
                correlationId);
        }
    }
}

public static class SignalExpirySchedulerExtensions
{
    public static IServiceCollection AddSignalExpiryScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue("SignalExpiry:Enabled", false);
        var baseUrl = configuration["SignalExpiry:SignalServiceBaseUrl"]
            ?? "http://localhost:5102";
        var apiKey = configuration["SignalExpiry:InternalApiKey"];
        var intervalSeconds = configuration.GetValue("SignalExpiry:IntervalSeconds", 30);
        var batchSize = configuration.GetValue("SignalExpiry:BatchSize", 100);
        var timeoutSeconds = configuration.GetValue("SignalExpiry:TimeoutSeconds", 10);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "SignalExpiry:SignalServiceBaseUrl must be an absolute URI.");
        }

        if (enabled && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "SignalExpiry:InternalApiKey is required when scheduling is enabled.");
        }

        if (intervalSeconds < 5)
        {
            throw new InvalidOperationException(
                "SignalExpiry:IntervalSeconds must be at least five seconds.");
        }

        if (batchSize is < 1 or > 500)
        {
            throw new InvalidOperationException(
                "SignalExpiry:BatchSize must be between 1 and 500.");
        }

        if (timeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                "SignalExpiry:TimeoutSeconds must be at least one second.");
        }

        var options = new SignalExpiryOptions
        {
            Enabled = enabled,
            SignalServiceBaseUrl = baseUri,
            InternalApiKey = apiKey,
            IntervalSeconds = intervalSeconds,
            BatchSize = batchSize,
            TimeoutSeconds = timeoutSeconds,
        };

        services.AddSingleton(options);
        services.AddSingleton<SignalExpiryJobState>();
        services.AddHttpClient<SignalExpiryClient>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        services.AddHostedService<SignalExpiryWorker>();
        return services;
    }
}
