using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Signal.Service;

const string serviceName = "ThesisPulse.Signal.Service";
const string consumerName = "ThesisPulse.Signal.Service.SignalGeneratedV1";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddThesisPulseMessaging(builder.Configuration, serviceName);
builder.Services.AddSingleton<SignalIntakeRegistry>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints(serviceName);

app.MapGet("/api/v1/status", (IConfiguration configuration) => Results.Ok(new
{
    mode = "FOUNDATION",
    environment = "PAPER",
    signalIntakeEnabled = true,
    signalPublishingEnabled = false,
    signalPersistence = "INBOX_AND_LOCAL_REGISTRY",
    messagingProvider = configuration["Messaging:Provider"] ?? "InMemory",
    contractVersion = SignalContractV1.ContractVersion,
}));

app.MapPost(
    "/api/v1/signals/intake",
    async (
        EventEnvelope<SignalGeneratedV1> envelope,
        InboxMessageProcessor processor,
        SignalIntakeRegistry registry,
        CancellationToken cancellationToken) =>
    {
        var metadataErrors = ValidateMetadata(envelope.Metadata);
        var signalErrors = SignalGeneratedV1Validator.Validate(envelope.Payload);
        var errors = metadataErrors.Concat(signalErrors).ToArray();

        if (errors.Length > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["signal"] = errors,
            });
        }

        var result = await processor.ProcessAsync(
            envelope,
            consumerName,
            registry.AddAsync,
            cancellationToken);

        return result.Outcome switch
        {
            InboxProcessingOutcome.Processed => Results.Accepted(
                $"/api/v1/signals/{envelope.Payload.SignalUid}",
                new
                {
                    status = "ACCEPTED",
                    messageId = result.MessageId,
                    signalUid = envelope.Payload.SignalUid,
                }),
            InboxProcessingOutcome.Duplicate => Results.Ok(new
            {
                status = "DUPLICATE_IGNORED",
                messageId = result.MessageId,
                signalUid = envelope.Payload.SignalUid,
            }),
            _ => Results.Problem(
                title: "Signal processing failed",
                detail: result.Error,
                statusCode: StatusCodes.Status500InternalServerError),
        };
    });

app.MapGet(
    "/api/v1/signals",
    (SignalIntakeRegistry registry, int? limit) =>
    {
        var requestedLimit = limit ?? 50;
        return Results.Ok(new
        {
            signals = registry.GetLatest(requestedLimit),
            count = Math.Min(requestedLimit, registry.GetLatest(requestedLimit).Count),
        });
    });

app.Run();

static IReadOnlyCollection<string> ValidateMetadata(MessageMetadata metadata)
{
    var errors = new List<string>();

    if (metadata.MessageId == Guid.Empty)
    {
        errors.Add("metadata.messageId must not be empty");
    }

    if (!metadata.EventType.Equals(
            SignalContractV1.EventType,
            StringComparison.OrdinalIgnoreCase))
    {
        errors.Add($"metadata.eventType must be {SignalContractV1.EventType}");
    }

    if (!metadata.ContractVersion.Equals(
            SignalContractV1.ContractVersion,
            StringComparison.OrdinalIgnoreCase))
    {
        errors.Add(
            $"metadata.contractVersion must be {SignalContractV1.ContractVersion}");
    }

    if (!metadata.Environment.Equals("PAPER", StringComparison.OrdinalIgnoreCase))
    {
        errors.Add("Phase 1 signal intake accepts PAPER messages only");
    }

    if (string.IsNullOrWhiteSpace(metadata.CorrelationId))
    {
        errors.Add("metadata.correlationId is required");
    }

    if (string.IsNullOrWhiteSpace(metadata.Producer))
    {
        errors.Add("metadata.producer is required");
    }

    if (string.IsNullOrWhiteSpace(metadata.ProducerVersion))
    {
        errors.Add("metadata.producerVersion is required");
    }

    if (string.IsNullOrWhiteSpace(metadata.ConfigurationVersion))
    {
        errors.Add("metadata.configurationVersion is required");
    }

    return errors;
}

public partial class Program
{
}
