using ThesisPulse.Portfolio.Service;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Infrastructure.Portfolio;
using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();

var persistenceProvider = builder.Configuration["PortfolioPersistence:Provider"] ?? "InMemory";
if (persistenceProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IPortfolioLedgerStore, InMemoryPortfolioLedgerStore>();
}
else if (persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("OperationalDatabase");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "OperationalDatabase connection string is required for SQL portfolio persistence.");
    }

    var options = new SqlServerPortfolioLedgerOptions
    {
        ConnectionString = connectionString,
        Actor = builder.Configuration["PortfolioPersistence:Actor"]
            ?? "ThesisPulse.Portfolio.Service",
        SourceVersion = builder.Configuration["PortfolioPersistence:SourceVersion"]
            ?? "1.0.0",
        CommandTimeoutSeconds = builder.Configuration.GetValue(
            "PortfolioPersistence:CommandTimeoutSeconds",
            30),
    };
    options.Validate();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IPortfolioLedgerStore, SqlServerPortfolioLedgerStore>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported portfolio persistence provider '{persistenceProvider}'.");
}

var automaticProjectionOptions = builder.Configuration
    .GetSection(AutomaticPortfolioFillProjectionOptions.SectionName)
    .Get<AutomaticPortfolioFillProjectionOptions>()
    ?? new AutomaticPortfolioFillProjectionOptions();
automaticProjectionOptions.Validate();
if (automaticProjectionOptions.Enabled &&
    !persistenceProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Automatic PAPER fill portfolio projection requires SQL Server persistence.");
}

builder.Services.AddSingleton(automaticProjectionOptions);
builder.Services.AddSingleton<AutomaticPortfolioFillProjectionWorkerState>();
if (automaticProjectionOptions.Enabled)
{
    builder.Services.AddSingleton<IAutomaticPortfolioFillProjectionCandidateStore,
        SqlServerAutomaticPortfolioFillProjectionCandidateStore>();
    builder.Services.AddSingleton<IAutomaticPortfolioFillProjectionWorkQueue,
        SqlServerAutomaticPortfolioFillProjectionWorkQueue>();
    builder.Services.AddSingleton<AutomaticPortfolioFillProjectionProcessor>();
    builder.Services.AddHostedService<AutomaticPortfolioFillProjectionIntakeWorker>();
    builder.Services.AddHostedService<AutomaticPortfolioFillProjectionWorker>();
}

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Portfolio.Service");
app.MapGet("/api/v1/status", (
    AutomaticPortfolioFillProjectionWorkerState projectionState) => Results.Ok(new
{
    mode = "PERSISTENT_PORTFOLIO_LEDGER",
    environment = "PAPER",
    persistenceProvider,
    sqlServerSourceOfTruth = persistenceProvider.Equals(
        "SqlServer",
        StringComparison.OrdinalIgnoreCase),
    authority = "LEDGER_AND_RECONCILIATION_ONLY",
    fillProjectionAuthority = true,
    automaticFillProjectionAuthority = automaticProjectionOptions.Enabled,
    automaticFillProjectionPolicyVersion = automaticProjectionOptions.ProjectionPolicyVersion,
    automaticFillProjectionPollIntervalSeconds = automaticProjectionOptions.PollIntervalSeconds,
    automaticFillProjectionBatchSize = automaticProjectionOptions.BatchSize,
    automaticFillProjectionMaximumAttempts = automaticProjectionOptions.MaximumAttempts,
    automaticFillProjectionWorkerState = projectionState.Snapshot(),
    reconciliationAuthority = true,
    automaticCorrectionAuthority = false,
    valuationAuthority = false,
    markToMarketAuthority = false,
    marginAuthority = false,
    settlementAuthority = false,
    exitOrderAuthority = false,
    riskDecisionAuthority = false,
    executionAuthority = false,
    brokerAuthority = false,
    livePortfolioAuthority = false,
}));
app.MapGet(
    "/api/v1/portfolio/automatic-fill-projection/metrics",
    (AutomaticPortfolioFillProjectionWorkerState state) => Results.Ok(state.Snapshot()));
app.MapPost("/api/v1/portfolio/fills/project", async (
    PortfolioFillProjectionRequestV1 request,
    IPortfolioLedgerStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.ProjectFillAsync(request, cancellationToken);
    return result.Status is PortfolioLedgerContractV1.Projected or PortfolioLedgerContractV1.Duplicate
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});
app.MapGet("/api/v1/portfolio/{portfolioCode}", async (
    string portfolioCode,
    IPortfolioLedgerStore store,
    CancellationToken cancellationToken) =>
{
    var snapshot = await store.GetSnapshotAsync(
        portfolioCode,
        DateTimeOffset.UtcNow,
        cancellationToken);
    return snapshot is null
        ? Results.NotFound()
        : Results.Ok(snapshot);
});
app.MapPost("/api/v1/portfolio/reconcile", async (
    LedgerReconciliationRequestV1 request,
    IPortfolioLedgerStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.ReconcileAsync(request, cancellationToken);
    return Results.Ok(result);
});
app.Run();

public partial class Program
{
}
