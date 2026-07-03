using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Signal.Service;

const string serviceName = "ThesisPulse.Signal.Service";
const string frontendCorsPolicy = "Frontend";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .GetChildren()
    .Select(item => item.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Cast<string>()
    .ToArray();
if (allowedOrigins.Length == 0)
{
    allowedOrigins = new[] { "http://localhost:5173" };
}

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddThesisPulseMessaging(builder.Configuration, serviceName);
builder.Services.AddThesisPulseMarketDataConsumerState(builder.Configuration);
builder.Services.AddEventConsumerState();
builder.Services.AddThesisPulseSignalStore(builder.Configuration, serviceName);
builder.Services.AddSignalStreamPublisher(builder.Configuration);
builder.Services.AddSignalMaintenance(builder.Configuration);
builder.Services.AddOptionChainFusionRuntime(builder.Configuration);
builder.Services.AddSingleton<SignalIntakeCoordinator>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy(frontendCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.UseCors(frontendCorsPolicy);
app.MapThesisPulsePlatformEndpoints(serviceName);
app.MapSignalEndpoints();
app.MapSignalDecisionProjectionEndpoints();
app.MapSignalMaintenance();
app.MapMarketDataConsumerEndpoints();
app.MapHealthChecks("/health/ready");

app.MapGet("/api/v1/status", (
    IConfiguration configuration,
    OptionChainRuntimeOptions optionChainRuntime) => Results.Ok(new
{
    mode = "AUTHORITATIVE_SIGNAL_LIFECYCLE",
    environment = "PAPER",
    signalIntakeEnabled = true,
    fusionSignalIntakeEnabled = true,
    scannerEnabled = true,
    riskProjectionAuthority = false,
    tradePlanProjectionAuthority = false,
    executionAuthority = false,
    signalStatusTransitionsEnabled = true,
    signalMaintenanceEnabled = configuration.GetValue("SignalMaintenance:Enabled", false),
    signalPublishingEnabled = configuration.GetValue("SignalRealtime:Enabled", false),
    marketDataConsumerEnabled = configuration.GetValue("MarketDataConsumer:Enabled", false),
    optionChainFusionEnabled = optionChainRuntime.Enabled,
    optionChainPersistence = optionChainRuntime.PersistenceProvider,
    optionChainMaximumAgeSeconds = optionChainRuntime.MaximumAgeSeconds,
    optionChainMinimumConfidence = optionChainRuntime.MinimumConfidence,
    optionChainSelectionAuthority = false,
    optionChainExecutionAuthority = false,
    signalPersistence = configuration["SignalPersistence:Provider"] ?? "InMemory",
    messagingProvider = configuration["Messaging:Provider"] ?? "InMemory",
    creatorEngineCode = configuration["SignalPersistence:CreatorEngineCode"]
        ?? "THESIS_PULSE_THESIS_FUSION",
    contractVersion = SignalContractV1.ContractVersion,
    scannerContractVersion = SignalScannerContractV1.ContractVersion,
}));

app.Run();

public partial class Program
{
}
