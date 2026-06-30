using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.MarketData.Service;
using ThesisPulse.Shared.Infrastructure.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.MarketData;
using ThesisPulse.Shared.Observability.Hosting;

const string serviceName = "ThesisPulse.MarketData.Service";

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";

var operationsOptions = new MarketDataOperationsOptions
{
    Enabled = builder.Configuration.GetValue("MarketData:Operations:Enabled", false),
    InternalApiKey = builder.Configuration["MarketData:Operations:InternalApiKey"],
    MaximumHistoricalDays = builder.Configuration.GetValue(
        "MarketData:Operations:MaximumHistoricalDays",
        366),
};
operationsOptions.Validate();

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddThesisPulseMarketDataPersistence(
    builder.Configuration,
    serviceName);
builder.Services.AddUpstoxMarketDataAdapter(builder.Configuration);
builder.Services.AddSingleton(operationsOptions);
builder.Services.AddSingleton<MarketDataJobState>();
builder.Services.AddSingleton<MarketDataOrchestrator>();
builder.Services.AddSingleton<MarketDataRecoveryHealthState>();

if (builder.Configuration.GetValue("MarketData:Recovery:Enabled", false))
{
    builder.Services.AddHostedService<MarketDataRecoveryWorker>();
}

builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints(serviceName);
app.MapMarketDataEndpoints();

app.MapGet(
    "/api/v1/status",
    (
        IConfiguration configuration,
        IUpstoxLiveFeedHealthState liveFeedHealthState,
        MarketDataRecoveryHealthState recoveryHealthState) => Results.Ok(new
        {
            mode = "MARKET_DATA_RECOVERY",
            environment = "PAPER",
            provider = "UPSTOX",
            providerEnabled = configuration.GetValue("Upstox:Enabled", false),
            persistence = configuration["MarketData:Persistence:Provider"]
                ?? "InMemory",
            operationsEnabled = operationsOptions.Enabled,
            instrumentSynchronizationEnabled = operationsOptions.Enabled,
            historicalIngestionEnabled = operationsOptions.Enabled,
            liveFeedAuthorizationEnabled = operationsOptions.Enabled,
            liveFeedNormalizationEnabled = true,
            liveWebSocketWorkerEnabled = configuration.GetValue(
                "Upstox:LiveFeed:Enabled",
                false),
            recoveryWorkerEnabled = configuration.GetValue(
                "MarketData:Recovery:Enabled",
                false),
            liveFeed = liveFeedHealthState.GetSnapshot(),
            recovery = recoveryHealthState.GetSnapshot(),
            brokerConnectivityEnabled = configuration.GetValue(
                "Upstox:Enabled",
                false),
        }));

app.Run();

public partial class Program
{
}
