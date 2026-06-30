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
    MaximumHistoricalDays = builder.Configuration.GetValue("MarketData:Operations:MaximumHistoricalDays", 366),
};
operationsOptions.Validate();

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddThesisPulseMessaging(builder.Configuration, serviceName);
builder.Services.AddThesisPulseMarketDataPublication(builder.Configuration, serviceName);
builder.Services.AddThesisPulseMarketDataPersistence(builder.Configuration, serviceName);
builder.Services.AddUpstoxMarketDataAdapter(builder.Configuration);
builder.Services.AddMarketDataPublicationTransport(builder.Configuration);
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
app.MapMarketDataPublicationEndpoints();
app.MapGet("/api/v1/status", (
    IConfiguration configuration,
    IUpstoxLiveFeedHealthState liveFeedHealthState,
    MarketDataRecoveryHealthState recoveryHealthState,
    MarketDataPublicationOptions publication,
    MarketDataDispatchOptions dispatch) => Results.Ok(new
{
    mode = "MARKET_DATA_PUBLICATION",
    environment = "PAPER",
    provider = "UPSTOX",
    providerEnabled = configuration.GetValue("Upstox:Enabled", false),
    persistence = configuration["MarketData:Persistence:Provider"] ?? "InMemory",
    operationsEnabled = operationsOptions.Enabled,
    recoveryWorkerEnabled = configuration.GetValue("MarketData:Recovery:Enabled", false),
    publicationEnabled = publication.Enabled,
    publicationDispatchEnabled = dispatch.Enabled,
    replayEnabled = true,
    liveFeed = liveFeedHealthState.GetSnapshot(),
    recovery = recoveryHealthState.GetSnapshot(),
}));
app.Run();

public partial class Program
{
}
