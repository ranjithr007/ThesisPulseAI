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
builder.Services.AddPaperWorkflowOrchestration(builder.Configuration);
builder.Services.AddAutomaticPaperWorkflowIntake(builder.Configuration);
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
app.UseCors(frontendCorsPolicy);
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Operations.Service");
app.MapPaperWorkflowEndpoints();
app.MapAutomaticPaperWorkflowIntake();
app.MapExecutionOperationalState();

app.MapGet(
    "/api/v1/status",
    (
        SignalExpiryOptions expiryOptions,
        SignalExpiryJobState expiryState,
        PaperWorkflowOptions workflowOptions,
        AutomaticPaperWorkflowOptions automaticOptions,
        IConfiguration configuration) => Results.Ok(new
    {
        mode = "PAPER_INTEGRATION_ORCHESTRATION",
        environment = "PAPER",
        schedulerEnabled = expiryOptions.Enabled,
        signalExpiry = new
        {
            enabled = expiryOptions.Enabled,
            intervalSeconds = expiryOptions.IntervalSeconds,
            batchSize = expiryOptions.BatchSize,
            signalServiceBaseUrl = expiryOptions.SignalServiceBaseUrl.ToString(),
            lastRun = expiryState.GetSnapshot(),
        },
        paperWorkflow = new
        {
            enabled = workflowOptions.Enabled,
            persistenceProvider = configuration["PaperWorkflowPersistence:Provider"]
                ?? "InMemory",
            maximumAttempts = workflowOptions.MaximumAttempts,
            retryDelaySeconds = workflowOptions.RetryDelaySeconds,
            recoveryIntervalSeconds = workflowOptions.RecoveryIntervalSeconds,
            recoveryBatchSize = workflowOptions.RecoveryBatchSize,
            authority = "ORCHESTRATION_ONLY",
            thesisAuthority = false,
            riskAuthority = false,
            tradePlanAuthority = false,
            executionAuthority = false,
            brokerAuthority = false,
            liveExecutionAuthority = false,
        },
        automaticIntelligenceIntake = new
        {
            enabled = automaticOptions.Enabled,
            portfolioCode = automaticOptions.PortfolioCode,
            primaryTimeframe = "5m",
            newExposureEnabled = automaticOptions.NewExposureEnabled,
            sessionCalendarHealthy = automaticOptions.SessionCalendarHealthy,
            authority = "INTAKE_AND_ENRICHMENT_ONLY",
            liveExecutionAuthority = false,
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
