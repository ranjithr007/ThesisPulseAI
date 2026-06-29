using ThesisPulse.Operations.Service;
using ThesisPulse.Shared.Observability.Hosting;

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
builder.Services.AddSignalExpiryScheduler(builder.Configuration);
builder.Services.AddPlatformHealthAggregation(builder.Configuration);
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
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Operations.Service");

app.MapGet(
    "/api/v1/status",
    (SignalExpiryOptions options, SignalExpiryJobState state) => Results.Ok(new
    {
        mode = "FOUNDATION",
        environment = "PAPER",
        schedulerEnabled = options.Enabled,
        signalExpiry = new
        {
            enabled = options.Enabled,
            intervalSeconds = options.IntervalSeconds,
            batchSize = options.BatchSize,
            signalServiceBaseUrl = options.SignalServiceBaseUrl.ToString(),
            lastRun = state.GetSnapshot(),
        },
        healthAggregationEnabled = true,
    }));

app.MapGet(
    "/api/v1/jobs/signal-expiry",
    (SignalExpiryOptions options, SignalExpiryJobState state) => Results.Ok(new
    {
        enabled = options.Enabled,
        intervalSeconds = options.IntervalSeconds,
        batchSize = options.BatchSize,
        lastRun = state.GetSnapshot(),
    }));

app.MapGet(
    "/api/v1/platform/health",
    async (
        PlatformHealthAggregator aggregator,
        CancellationToken cancellationToken) =>
    {
        var snapshot = await aggregator.CheckAsync(cancellationToken);
        return snapshot.Status == "UNHEALTHY"
            ? Results.Json(snapshot, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(snapshot);
    });

app.Run();

public partial class Program
{
}
