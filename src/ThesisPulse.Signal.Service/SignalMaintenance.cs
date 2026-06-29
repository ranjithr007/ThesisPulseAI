using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public sealed record SignalMaintenanceOptions
{
    public bool Enabled { get; init; }

    public string? InternalApiKey { get; init; }
}

public static class SignalMaintenanceExtensions
{
    public static IServiceCollection AddSignalMaintenance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue("SignalMaintenance:Enabled", false);
        var apiKey = configuration["SignalMaintenance:InternalApiKey"];

        if (enabled && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "SignalMaintenance:InternalApiKey is required when maintenance is enabled.");
        }

        services.AddSingleton(new SignalMaintenanceOptions
        {
            Enabled = enabled,
            InternalApiKey = apiKey,
        });
        return services;
    }

    public static IEndpointRouteBuilder MapSignalMaintenance(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/v1/signals/expire-due",
            async (
                ExpireDueSignalsRequestV1 request,
                HttpRequest httpRequest,
                SignalMaintenanceOptions options,
                IDueSignalMaintenanceStore maintenanceStore,
                ISignalStatusStore statusStore,
                ISignalStreamPublisher streamPublisher,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.Problem(
                        title: "Signal maintenance is disabled",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (!IsAuthorized(httpRequest, options.InternalApiKey!))
                {
                    return Results.Unauthorized();
                }

                var errors = Validate(request);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = errors.ToArray(),
                    });
                }

                var result = await maintenanceStore.ExpireDueAsync(
                    request,
                    cancellationToken);
                var published = 0;
                var publicationFailures = 0;

                foreach (var expiredSignal in result.Signals)
                {
                    var signal = await statusStore.GetAsync(
                        expiredSignal.SignalUid,
                        cancellationToken);
                    if (signal is null)
                    {
                        publicationFailures++;
                        continue;
                    }

                    var publication = await streamPublisher.PublishAsync(
                        signal,
                        expiredSignal.EventSequence,
                        request.AsOfUtc,
                        request.CorrelationId,
                        cancellationToken);

                    if (publication.Outcome == SignalStreamPublishOutcome.Published)
                    {
                        published++;
                    }
                    else if (publication.Outcome == SignalStreamPublishOutcome.Failed)
                    {
                        publicationFailures++;
                    }
                }

                return Results.Ok(new
                {
                    status = "COMPLETED",
                    result.AsOfUtc,
                    result.Selected,
                    result.Expired,
                    published,
                    publicationFailures,
                    result.Signals,
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

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedValues.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static IReadOnlyCollection<string> Validate(
        ExpireDueSignalsRequestV1 request)
    {
        var errors = new List<string>();

        if (request.AsOfUtc == default)
        {
            errors.Add("asOfUtc is required");
        }

        if (request.MaximumCount is < 1 or > 500)
        {
            errors.Add("maximumCount must be between 1 and 500");
        }

        if (string.IsNullOrWhiteSpace(request.SourceService) ||
            string.IsNullOrWhiteSpace(request.SourceVersion) ||
            string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            errors.Add("sourceService, sourceVersion, and correlationId are required");
        }

        return errors;
    }
}
