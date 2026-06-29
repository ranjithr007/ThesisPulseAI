using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Trading.Api;

public sealed class SignalStreamHub : Hub;

public sealed record TradingSignalStreamOptions
{
    public bool IngestionEnabled { get; init; }

    public string? InternalApiKey { get; init; }

    public int BufferCapacity { get; init; } = 100;
}

public sealed class SignalStreamBuffer(TradingSignalStreamOptions options)
{
    private readonly ConcurrentQueue<SignalStreamEventV1> _events = new();

    public void Add(SignalStreamEventV1 streamEvent)
    {
        _events.Enqueue(streamEvent);

        while (_events.Count > options.BufferCapacity &&
               _events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyCollection<SignalStreamEventV1> GetLatest(int maximumCount)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return _events
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(maximumCount)
            .ToArray();
    }
}

public static class SignalStreamServiceCollectionExtensions
{
    public static IServiceCollection AddTradingSignalStream(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var enabled = configuration.GetValue("SignalStream:IngestionEnabled", false);
        var apiKey = configuration["SignalStream:InternalApiKey"];
        var capacity = configuration.GetValue("SignalStream:BufferCapacity", 100);

        if (enabled && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "SignalStream:InternalApiKey is required when ingestion is enabled.");
        }

        if (capacity is < 1 or > 10_000)
        {
            throw new InvalidOperationException(
                "SignalStream:BufferCapacity must be between 1 and 10000.");
        }

        services.AddSignalR();
        services.AddSingleton(new TradingSignalStreamOptions
        {
            IngestionEnabled = enabled,
            InternalApiKey = apiKey,
            BufferCapacity = capacity,
        });
        services.AddSingleton<SignalStreamBuffer>();
        return services;
    }
}

public static class SignalStreamEndpointExtensions
{
    public static IEndpointRouteBuilder MapTradingSignalStream(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<SignalStreamHub>("/hubs/signals");

        endpoints.MapGet(
            "/api/v1/stream/signals/status",
            (TradingSignalStreamOptions options) => Results.Ok(new
            {
                environment = "PAPER",
                ingestionEnabled = options.IngestionEnabled,
                hubPath = "/hubs/signals",
                clientMethod = "signalUpdated",
                contractVersion = SignalStreamContractV1.ContractVersion,
            }));

        endpoints.MapGet(
            "/api/v1/stream/signals/recent",
            (SignalStreamBuffer buffer, int? limit) =>
            {
                var events = buffer.GetLatest(limit ?? 50);
                return Results.Ok(new
                {
                    events,
                    count = events.Count,
                });
            });

        endpoints.MapPost(
            "/internal/v1/signals/events",
            async (
                SignalStreamEventV1 streamEvent,
                HttpRequest request,
                TradingSignalStreamOptions options,
                SignalStreamBuffer buffer,
                IHubContext<SignalStreamHub> hubContext,
                CancellationToken cancellationToken) =>
            {
                if (!options.IngestionEnabled)
                {
                    return Results.Problem(
                        title: "Signal stream ingestion is disabled",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (!IsAuthorized(request, options.InternalApiKey!))
                {
                    return Results.Unauthorized();
                }

                var validationErrors = Validate(streamEvent);
                if (validationErrors.Count > 0)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["streamEvent"] = validationErrors.ToArray(),
                    });
                }

                buffer.Add(streamEvent);
                await hubContext.Clients.All.SendAsync(
                    "signalUpdated",
                    streamEvent,
                    cancellationToken);

                return Results.Accepted(value: new
                {
                    status = "BROADCAST",
                    streamEvent.EventUid,
                    streamEvent.SignalUid,
                    streamEvent.Status,
                    streamEvent.StatusSequence,
                });
            });

        return endpoints;
    }

    private static bool IsAuthorized(HttpRequest request, string expectedKey)
    {
        if (!request.Headers.TryGetValue(
                "X-ThesisPulse-Internal-Key",
                out var suppliedValues))
        {
            return false;
        }

        var suppliedKey = suppliedValues.ToString();
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static IReadOnlyCollection<string> Validate(
        SignalStreamEventV1 streamEvent)
    {
        var errors = new List<string>();

        if (streamEvent.EventUid == Guid.Empty)
        {
            errors.Add("eventUid must not be empty");
        }

        if (streamEvent.SignalUid == Guid.Empty)
        {
            errors.Add("signalUid must not be empty");
        }

        if (!streamEvent.EventType.Equals(
                SignalStreamContractV1.EventType,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"eventType must be {SignalStreamContractV1.EventType}");
        }

        if (!streamEvent.ContractVersion.Equals(
                SignalStreamContractV1.ContractVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"contractVersion must be {SignalStreamContractV1.ContractVersion}");
        }

        if (!SignalStatusV1.Values.Contains(streamEvent.Status))
        {
            errors.Add("status is not supported");
        }

        if (streamEvent.StatusSequence < 0)
        {
            errors.Add("statusSequence must not be negative");
        }

        if (string.IsNullOrWhiteSpace(streamEvent.InstrumentKey) ||
            string.IsNullOrWhiteSpace(streamEvent.CorrelationId))
        {
            errors.Add("instrumentKey and correlationId are required");
        }

        return errors;
    }
}
