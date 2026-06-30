using ThesisPulse.Shared.Infrastructure.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.MarketData;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Trading.Api;

const string frontendCorsPolicy = "Frontend";
const string serviceName = "ThesisPulse.Trading.Api";

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton<IMarketDataConsumerSink, TradingMarketDataSink>();
builder.Services.AddTradingSignalStream(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy(frontendCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.UseCors(frontendCorsPolicy);
app.MapThesisPulsePlatformEndpoints(serviceName, contractVersion: "v1");
app.MapTradingSignalStream();
app.MapMarketDataStream();
app.MapGet("/api/v1/platform", () => Results.Ok(new
{
    name = "ThesisPulse AI",
    tagline = "Intelligent signals. Validated theses. Adaptive decisions.",
    environment = "PAPER",
    liveExecutionEnabled = false,
    marketDataStreamingEnabled = true,
}));
app.Run();

public partial class Program
{
}
