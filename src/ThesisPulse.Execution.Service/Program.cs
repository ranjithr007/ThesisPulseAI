using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);
var requestedEnvironment = builder.Configuration["Platform:Environment"] ?? "PAPER";
var liveExecutionEnabled = builder.Configuration.GetValue<bool>("Platform:LiveExecutionEnabled");

if (!string.Equals(requestedEnvironment, "PAPER", StringComparison.OrdinalIgnoreCase) || liveExecutionEnabled)
{
    throw new InvalidOperationException(
        "Phase 1 Execution Service must run in PAPER mode with live execution disabled.");
}

builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Execution.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "FOUNDATION",
    environment = "PAPER",
    brokerSubmissionEnabled = false,
    acceptsOrderIntents = false,
    requiresApprovedRiskDecision = true,
}));
app.Run();

public partial class Program
{
}
