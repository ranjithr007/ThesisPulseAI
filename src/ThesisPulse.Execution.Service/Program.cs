using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
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
builder.Services.Configure<DeterministicPaperExecutionOptions>(
    builder.Configuration.GetSection(DeterministicPaperExecutionOptions.SectionName));
builder.Services.AddSingleton<IPaperExecutionService, DeterministicPaperExecutionService>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Execution.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "DETERMINISTIC_PAPER_EXECUTION",
    environment = ExecutionCommandContractV1.PaperEnvironment,
    acceptsReadyTradePlans = true,
    executionCommandAuthority = true,
    paperSubmissionAuthority = true,
    brokerSubmissionAuthority = false,
    liveExecutionAuthority = false,
    idempotencyRequired = true,
}));
app.MapPost("/api/v1/execution/commands", (
    ExecutionCommandRequestV1 request,
    IPaperExecutionService executionService) =>
{
    var result = executionService.Authorize(request);
    return result.Status == ExecutionCommandContractV1.Authorized
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});
app.MapGet("/api/v1/paper-orders/{paperOrderUid:guid}", (
    Guid paperOrderUid,
    IPaperExecutionService executionService) =>
{
    var order = executionService.GetOrder(paperOrderUid);
    return order is null
        ? Results.NotFound()
        : Results.Ok(order);
});
app.MapPost("/api/v1/paper-orders/{paperOrderUid:guid}/events", (
    Guid paperOrderUid,
    PaperOrderEventRequestV1 request,
    IPaperExecutionService executionService) =>
{
    var result = executionService.ApplyEvent(paperOrderUid, request);
    return result.Applied
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});
app.Run();

public partial class Program
{
}
