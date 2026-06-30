using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Infrastructure.Execution;
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
builder.Services.AddSingleton<DeterministicPaperExecutionService>();

var persistenceProvider = builder.Configuration["PaperExecutionPersistence:Provider"] ?? "InMemory";
if (persistenceProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IPaperExecutionLedgerStore, InMemoryPaperExecutionLedgerStore>();
}
else if (persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("OperationalDatabase");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "OperationalDatabase connection string is required for SQL persistence.");
    }

    var storeOptions = new SqlServerPaperExecutionLedgerOptions
    {
        ConnectionString = connectionString,
        BrokerAccountReference = builder.Configuration[
            "PaperExecutionPersistence:BrokerAccountReference"] ?? "PAPER-PRIMARY",
        CurrencyCode = builder.Configuration[
            "PaperExecutionPersistence:CurrencyCode"] ?? "INR",
        Actor = builder.Configuration[
            "PaperExecutionPersistence:Actor"] ?? "ThesisPulse.Execution.Service",
        SourceVersion = builder.Configuration[
            "PaperExecutionPersistence:SourceVersion"] ?? "1.0.0",
        CommandTimeoutSeconds = builder.Configuration.GetValue(
            "PaperExecutionPersistence:CommandTimeoutSeconds",
            30),
    };
    storeOptions.Validate();
    builder.Services.AddSingleton(storeOptions);
    builder.Services.AddSingleton<IPaperExecutionLedgerStore, SqlServerPaperExecutionLedgerStore>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported paper execution persistence provider '{persistenceProvider}'.");
}

builder.Services.AddSingleton<IPaperExecutionService, PersistentPaperExecutionService>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Execution.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "PERSISTENT_PAPER_EXECUTION",
    environment = ExecutionCommandContractV1.PaperEnvironment,
    persistenceProvider,
    sqlServerSourceOfTruth = persistenceProvider.Equals(
        "SqlServer",
        StringComparison.OrdinalIgnoreCase),
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
