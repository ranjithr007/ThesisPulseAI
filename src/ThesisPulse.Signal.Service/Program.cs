using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.DependencyInjection;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Signal.Service;

const string serviceName = "ThesisPulse.Signal.Service";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddThesisPulseMessaging(builder.Configuration, serviceName);
builder.Services.AddThesisPulseSignalStore(builder.Configuration, serviceName);
builder.Services.AddSingleton<SignalIntakeCoordinator>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints(serviceName);
app.MapSignalEndpoints();

app.MapGet("/api/v1/status", (IConfiguration configuration) => Results.Ok(new
{
    mode = "FOUNDATION",
    environment = "PAPER",
    signalIntakeEnabled = true,
    signalPublishingEnabled = false,
    signalPersistence = configuration["SignalPersistence:Provider"] ?? "InMemory",
    messagingProvider = configuration["Messaging:Provider"] ?? "InMemory",
    creatorEngineCode = configuration["SignalPersistence:CreatorEngineCode"]
        ?? "THESIS_PULSE_MOCK_FUSION",
    contractVersion = SignalContractV1.ContractVersion,
}));

app.Run();

public partial class Program
{
}
