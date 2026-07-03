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

var lifecycleReadOptions = builder.Configuration
    .GetSection(PaperTradeLifecycleReadOptions.SectionName)
    .Get<PaperTradeLifecycleReadOptions>() ?? new PaperTradeLifecycleReadOptions();
lifecycleReadOptions.Validate();
builder.Services.AddSingleton(lifecycleReadOptions);

var persistenceProvider = builder.Configuration["PaperExecutionPersistence:Provider"] ?? "InMemory";
if (persistenceProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IPaperExecutionLedgerStore, InMemoryPaperExecutionLedgerStore>();
    builder.Services.AddSingleton<IPaperTradeLifecycleReadStore,
        UnavailablePaperTradeLifecycleReadStore>();
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
    builder.Services.AddSingleton<IPaperExecutionLedgerStore,
        SqlServerPaperExecutionLedgerDecorator>();
    builder.Services.AddSingleton<IPaperTradeLifecycleReadStore,
        SqlServerPaperTradeLifecycleReadStore>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported paper execution persistence provider '{persistenceProvider}'.");
}

var automaticOptions = builder.Configuration
    .GetSection(AutomaticPaperExecutionOptions.SectionName)
    .Get<AutomaticPaperExecutionOptions>() ?? new AutomaticPaperExecutionOptions();
automaticOptions.Validate();
if (automaticOptions.Enabled &&
    !persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Automatic PLAN_READY-to-PAPER execution requires SQL Server persistence.");
}

var submissionOptions = builder.Configuration
    .GetSection(AutomaticPaperSubmissionOptions.SectionName)
    .Get<AutomaticPaperSubmissionOptions>() ?? new AutomaticPaperSubmissionOptions();
submissionOptions.Validate();
if (submissionOptions.Enabled &&
    !persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Automatic PAPER submission requires SQL Server persistence.");
}

var fillOptions = builder.Configuration
    .GetSection(AutomaticPaperFillOptions.SectionName)
    .Get<AutomaticPaperFillOptions>() ?? new AutomaticPaperFillOptions();
fillOptions.Validate();
if (fillOptions.Enabled &&
    !persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Automatic PAPER fill simulation requires SQL Server persistence.");
}

builder.Services.AddSingleton(automaticOptions);
builder.Services.AddSingleton(submissionOptions);
builder.Services.AddSingleton(fillOptions);
builder.Services.AddSingleton<AutomaticPaperExecutionWorkerState>();
builder.Services.AddSingleton<AutomaticPaperSubmissionWorkerState>();
builder.Services.AddSingleton<AutomaticPaperFillWorkerState>();
builder.Services.AddSingleton<IPaperExecutionService, PersistentPaperExecutionService>();

if (automaticOptions.Enabled)
{
    builder.Services.AddSingleton<IAutomaticPaperExecutionCandidateStore,
        SqlServerAutomaticPaperExecutionCandidateStore>();
    builder.Services.AddSingleton<IAutomaticPaperExecutionWorkQueue,
        SqlServerAutomaticPaperExecutionWorkQueue>();
    builder.Services.AddHttpClient<IAutomaticPaperExecutionContextProvider,
        HttpAutomaticPaperExecutionContextProvider>(client =>
    {
        client.BaseAddress = new Uri(automaticOptions.OperationsServiceBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(automaticOptions.TimeoutSeconds);
    });
    builder.Services.AddSingleton<AutomaticPaperExecutionProcessor>();
    builder.Services.AddHostedService<AutomaticPaperExecutionIntakeWorker>();
    builder.Services.AddHostedService<AutomaticPaperExecutionWorker>();
}

if (submissionOptions.Enabled)
{
    builder.Services.AddSingleton<IAutomaticPaperSubmissionCandidateStore,
        SqlServerAutomaticPaperSubmissionCandidateStore>();
    builder.Services.AddSingleton<IAutomaticPaperSubmissionWorkQueue,
        SqlServerAutomaticPaperSubmissionWorkQueue>();
    builder.Services.AddHttpClient<IAutomaticPaperSubmissionContextProvider,
        HttpAutomaticPaperSubmissionContextProvider>(client =>
    {
        client.BaseAddress = new Uri(submissionOptions.OperationsServiceBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(submissionOptions.TimeoutSeconds);
    });
    builder.Services.AddSingleton<AutomaticPaperSubmissionProcessor>();
    builder.Services.AddHostedService<AutomaticPaperSubmissionIntakeWorker>();
    builder.Services.AddHostedService<AutomaticPaperSubmissionWorker>();
}

if (fillOptions.Enabled)
{
    builder.Services.AddSingleton<IAutomaticPaperFillCandidateStore,
        SqlServerAutomaticPaperFillCandidateStore>();
    builder.Services.AddSingleton<IAutomaticPaperFillWorkQueue,
        SqlServerAutomaticPaperFillWorkQueue>();
    builder.Services.AddHttpClient<IAutomaticPaperFillMarketDataProvider,
        HttpAutomaticPaperFillMarketDataProvider>(client =>
    {
        client.BaseAddress = new Uri(fillOptions.MarketDataServiceBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(fillOptions.TimeoutSeconds);
    });
    builder.Services.AddSingleton<DeterministicPaperFillPolicy>();
    builder.Services.AddSingleton<AutomaticPaperFillProcessor>();
    builder.Services.AddHostedService<AutomaticPaperFillIntakeWorker>();
    builder.Services.AddHostedService<AutomaticPaperFillWorker>();
}

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Execution.Service");
app.MapPaperTradeLifecycleEndpoints();
app.MapGet("/api/v1/status", (
    AutomaticPaperExecutionWorkerState automaticState,
    AutomaticPaperSubmissionWorkerState submissionState,
    AutomaticPaperFillWorkerState fillState) => Results.Ok(new
{
    mode = "PERSISTENT_PAPER_EXECUTION",
    environment = ExecutionCommandContractV1.PaperEnvironment,
    persistenceProvider,
    sqlServerSourceOfTruth = persistenceProvider.Equals(
        "SqlServer",
        StringComparison.OrdinalIgnoreCase),
    lifecycleReadModelAvailable = persistenceProvider.Equals(
        "SqlServer",
        StringComparison.OrdinalIgnoreCase),
    acceptsReadyTradePlans = true,
    executionCommandAuthority = true,
    paperOrderCreationAuthority = true,
    paperSubmissionAuthority = true,
    automaticPlanReadyIntakeEnabled = automaticOptions.Enabled,
    automaticPlanReadyPollIntervalSeconds = automaticOptions.PollIntervalSeconds,
    automaticPlanReadyBatchSize = automaticOptions.BatchSize,
    automaticPlanReadyMaximumAttempts = automaticOptions.MaximumAttempts,
    automaticPlanReadyWorkerState = automaticState.Snapshot(),
    automaticPaperSubmissionAuthority = submissionOptions.Enabled,
    automaticPaperSubmissionPollIntervalSeconds = submissionOptions.PollIntervalSeconds,
    automaticPaperSubmissionBatchSize = submissionOptions.BatchSize,
    automaticPaperSubmissionMaximumAttempts = submissionOptions.MaximumAttempts,
    automaticPaperSubmissionWorkerState = submissionState.Snapshot(),
    automaticPaperFillAuthority = fillOptions.Enabled,
    automaticPaperFillPolicyVersion = fillOptions.FillPolicyVersion,
    automaticPaperFillPollIntervalSeconds = fillOptions.PollIntervalSeconds,
    automaticPaperFillBatchSize = fillOptions.BatchSize,
    automaticPaperFillWorkerState = fillState.Snapshot(),
    automaticPartialFillAuthority = false,
    portfolioMutationAuthority = false,
    paperGateway = "INTERNAL_DETERMINISTIC",
    brokerSubmissionAuthority = false,
    liveExecutionAuthority = false,
    idempotencyRequired = true,
}));
app.MapGet(
    "/api/v1/execution/automatic/metrics",
    (AutomaticPaperExecutionWorkerState state) => Results.Ok(state.Snapshot()));
app.MapGet(
    "/api/v1/execution/automatic-submission/metrics",
    (AutomaticPaperSubmissionWorkerState state) => Results.Ok(state.Snapshot()));
app.MapGet(
    "/api/v1/execution/automatic-fill/metrics",
    (AutomaticPaperFillWorkerState state) => Results.Ok(state.Snapshot()));
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
