using ThesisPulse.Operations.Service;
using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddSignalExpiryScheduler(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
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

app.Run();

public partial class Program
{
}
