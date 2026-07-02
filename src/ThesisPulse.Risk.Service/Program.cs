using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.Configure<DeterministicRiskOptions>(
    builder.Configuration.GetSection(DeterministicRiskOptions.SectionName));
builder.Services.Configure<DeterministicTradePlanOptions>(
    builder.Configuration.GetSection(DeterministicTradePlanOptions.SectionName));

var persistenceOptions = builder.Configuration
    .GetSection(SignalRiskPersistenceOptions.SectionName)
    .Get<SignalRiskPersistenceOptions>() ?? new SignalRiskPersistenceOptions();
persistenceOptions.Validate();
builder.Services.AddSingleton(persistenceOptions);

var workerOptions = builder.Configuration
    .GetSection(SignalRiskWorkerOptions.SectionName)
    .Get<SignalRiskWorkerOptions>() ?? new SignalRiskWorkerOptions();
workerOptions.Validate();
if (workerOptions.Enabled && !persistenceOptions.UseSqlServer)
    throw new InvalidOperationException("Signal Risk worker requires SQL_SERVER persistence mode.");
builder.Services.AddSingleton(workerOptions);

var portfolioRiskOptions = builder.Configuration
    .GetSection(AutomaticPortfolioRiskOptions.SectionName)
    .Get<AutomaticPortfolioRiskOptions>() ?? new AutomaticPortfolioRiskOptions();
portfolioRiskOptions.Validate();
if (portfolioRiskOptions.Enabled && !persistenceOptions.UseSqlServer)
    throw new InvalidOperationException("Automatic Portfolio Risk requires SQL_SERVER persistence mode.");
builder.Services.AddSingleton(portfolioRiskOptions);

var canonicalIntakeOptions = builder.Configuration
    .GetSection(CanonicalSignalRiskIntakeOptions.SectionName)
    .Get<CanonicalSignalRiskIntakeOptions>() ?? new CanonicalSignalRiskIntakeOptions();
canonicalIntakeOptions.Validate();
if (canonicalIntakeOptions.Enabled && (!persistenceOptions.UseSqlServer || !workerOptions.Enabled))
    throw new InvalidOperationException("Canonical Signal Risk intake requires SQL_SERVER persistence and the Signal Risk worker.");
builder.Services.AddSingleton(canonicalIntakeOptions);

var tradePlanWorkerOptions = builder.Configuration
    .GetSection(AutomaticTradePlanWorkerOptions.SectionName)
    .Get<AutomaticTradePlanWorkerOptions>() ?? new AutomaticTradePlanWorkerOptions();
tradePlanWorkerOptions.Validate();
if (tradePlanWorkerOptions.Enabled && !persistenceOptions.UseSqlServer)
    throw new InvalidOperationException("Automatic Trade Plan worker requires SQL_SERVER persistence mode.");
builder.Services.AddSingleton(tradePlanWorkerOptions);

var tradePlanIntakeOptions = builder.Configuration
    .GetSection(AutomaticTradePlanIntakeOptions.SectionName)
    .Get<AutomaticTradePlanIntakeOptions>() ?? new AutomaticTradePlanIntakeOptions();
tradePlanIntakeOptions.Validate();
if (tradePlanIntakeOptions.Enabled && (!persistenceOptions.UseSqlServer || !tradePlanWorkerOptions.Enabled))
    throw new InvalidOperationException("Automatic Trade Plan intake requires SQL_SERVER persistence and the Automatic Trade Plan worker.");
builder.Services.AddSingleton(tradePlanIntakeOptions);

builder.Services.AddSingleton<IRiskDecisionEngine, DeterministicRiskDecisionEngine>();
builder.Services.AddSingleton<ISignalRiskProjector, DeterministicSignalRiskProjector>();
builder.Services.AddSingleton<IAutomaticTradePlanProjector, DeterministicAutomaticTradePlanProjector>();
builder.Services.AddSingleton<IAutomaticTradePlanLifecycleStore, InMemoryAutomaticTradePlanLifecycleStore>();
builder.Services.AddSingleton<AutomaticTradePlanCoordinator>();
builder.Services.AddSingleton<ITradePlanBuilder, DeterministicTradePlanBuilder>();
builder.Services.AddSingleton<SignalRiskWorkerState>();
builder.Services.AddSingleton<AutomaticTradePlanWorkerState>();
builder.Services.AddSingleton<AutomaticTradePlanWorkProcessor>();
builder.Services.AddSingleton<AutomaticPortfolioRiskWorkerState>();

if (persistenceOptions.UseSqlServer)
{
    builder.Services.AddSingleton<ISignalRiskEvaluationStore, SqlServerSignalRiskEvaluationStore>();
    builder.Services.AddSingleton<ISignalRiskWorkQueue, SqlServerSignalRiskWorkQueue>();
    builder.Services.AddSingleton<ISignalRiskOperationalStatusStore, SqlServerSignalRiskOperationalStatusStore>();
    builder.Services.AddSingleton<ISignalRiskMetricsStore, SqlServerSignalRiskMetricsStore>();
    builder.Services.AddSingleton<ICanonicalSignalRiskCandidateStore, SqlServerCanonicalSignalRiskCandidateStore>();
    builder.Services.AddSingleton<IAutomaticTradePlanCandidateStore, SqlServerAutomaticTradePlanCandidateStore>();
    builder.Services.AddSingleton<IAutomaticTradePlanWorkQueue, SqlServerAutomaticTradePlanWorkQueue>();
    builder.Services.AddSingleton<IAutomaticTradePlanResultStore, SqlServerAutomaticTradePlanResultStore>();
    builder.Services.AddSingleton<IAutomaticTradePlanMetricsStore, SqlServerAutomaticTradePlanMetricsStore>();
    builder.Services.AddSingleton<IAutomaticPortfolioRiskCandidateStore, SqlServerAutomaticPortfolioRiskCandidateStore>();
    builder.Services.AddSingleton<IPortfolioRiskEvaluationContextStore, SqlServerPortfolioRiskEvaluationContextStore>();
    builder.Services.AddSingleton<IAutomaticPortfolioRiskWorkQueue, SqlServerAutomaticPortfolioRiskWorkQueue>();
    builder.Services.AddSingleton<IPortfolioRiskSnapshotStore, SqlServerPortfolioRiskSnapshotStore>();
    builder.Services.AddSingleton<AutomaticPortfolioRiskProcessor>();
}
else
{
    builder.Services.AddSingleton<ISignalRiskEvaluationStore, InMemorySignalRiskEvaluationStore>();
    builder.Services.AddSingleton<ISignalRiskMetricsStore, InMemorySignalRiskMetricsStore>();
    builder.Services.AddSingleton<IAutomaticTradePlanMetricsStore, InMemoryAutomaticTradePlanMetricsStore>();
}

builder.Services.AddSingleton<SignalRiskCoordinator>();
if (workerOptions.Enabled)
    builder.Services.AddHostedService<SignalRiskWorker>();
if (portfolioRiskOptions.Enabled)
    builder.Services.AddHostedService<AutomaticPortfolioRiskWorker>();
if (canonicalIntakeOptions.Enabled)
{
    builder.Services.AddHttpClient<ICanonicalSignalRiskContextProvider, HttpCanonicalSignalRiskContextProvider>(client =>
        client.BaseAddress = new Uri(canonicalIntakeOptions.PortfolioServiceBaseUrl));
    builder.Services.AddHostedService<CanonicalSignalRiskIntakeWorker>();
}
if (tradePlanWorkerOptions.Enabled)
    builder.Services.AddHostedService<AutomaticTradePlanWorker>();
if (tradePlanIntakeOptions.Enabled)
    builder.Services.AddHostedService<AutomaticTradePlanIntakeWorker>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Risk.Service");

app.MapGet("/api/v1/status", (
    SignalRiskWorkerState workerState,
    AutomaticTradePlanWorkerState tradePlanState,
    AutomaticPortfolioRiskWorkerState portfolioRiskState) => Results.Ok(new
{
    mode = "DETERMINISTIC_RISK_AND_TRADE_PLAN",
    environment = "PAPER",
    failClosed = true,
    automaticSignalProjection = true,
    automaticRiskPersistence = true,
    automaticPortfolioRiskEnabled = portfolioRiskOptions.Enabled,
    automaticPortfolioRiskPollIntervalSeconds = portfolioRiskOptions.PollIntervalSeconds,
    automaticPortfolioRiskBatchSize = portfolioRiskOptions.BatchSize,
    automaticPortfolioRiskMaximumAttempts = portfolioRiskOptions.MaximumAttempts,
    automaticPortfolioRiskMaximumSourceAgeSeconds = portfolioRiskOptions.MaximumSourceAgeSeconds,
    automaticPortfolioRiskWorkerState = portfolioRiskState.Snapshot(),
    automaticTradePlanProjection = true,
    automaticTradePlanLifecycle = true,
    automaticTradePlanIntakeEnabled = tradePlanIntakeOptions.Enabled,
    automaticTradePlanWorkerEnabled = tradePlanWorkerOptions.Enabled,
    automaticTradePlanWorkerState = tradePlanState.Snapshot(),
    automaticRiskWorkerEnabled = workerOptions.Enabled,
    automaticCanonicalSignalIntakeEnabled = canonicalIntakeOptions.Enabled,
    automaticRiskWorkerState = workerState.Snapshot(),
    persistenceMode = persistenceOptions.UseSqlServer ? "SQL_SERVER" : "IN_MEMORY_PAPER",
    defaultRiskDecision = RiskDecisionContractV1.Rejected,
    riskDecisionAuthority = true,
    riskStatusAuthority = true,
    portfolioOperatingModeAuthority = portfolioRiskOptions.Enabled,
    tradePlanAuthority = true,
    positionSizingAuthority = true,
    executionAuthority = false,
    brokerSubmissionAuthority = false
}));

app.MapGet("/api/v1/risk/operations/metrics", async (
    ISignalRiskMetricsStore metricsStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await metricsStore.ReadAsync(cancellationToken)));

app.MapGet("/api/v1/risk/portfolio/operations/metrics", (
    AutomaticPortfolioRiskWorkerState state) => Results.Ok(state.Snapshot()));

if (persistenceOptions.UseSqlServer)
{
    app.MapGet("/api/v1/risk/portfolio/{portfolioCode}/control-state", async (
        string portfolioCode,
        CancellationToken cancellationToken) =>
    {
        const string sql = """
            SELECT portfolio_code, environment, risk_snapshot_uid, operating_mode,
                   effective_risk_multiplier, new_exposure_allowed,
                   risk_reducing_exit_allowed, source_as_of_utc, version_number,
                   updated_at_utc
            FROM risk.portfolio_control_states
            WHERE portfolio_code = @portfolio_code AND environment = 'PAPER';
            """;
        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = portfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return Results.NotFound(new { portfolioCode, environment = "PAPER", reason = "AUTHORITATIVE_RISK_STATE_NOT_AVAILABLE" });
        return Results.Ok(new
        {
            portfolioCode = reader.GetString(0),
            environment = reader.GetString(1),
            riskSnapshotUid = reader.GetGuid(2),
            operatingMode = reader.GetString(3),
            effectiveRiskMultiplier = reader.GetDecimal(4),
            newExposureAllowed = reader.GetBoolean(5),
            riskReducingExitAllowed = reader.GetBoolean(6),
            sourceAsOfUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
            versionNumber = reader.GetInt64(8),
            updatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)
        });
    });
}

app.MapGet("/api/v1/trade-plans/operations/metrics", async (
    IAutomaticTradePlanMetricsStore metricsStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await metricsStore.ReadAsync(cancellationToken)));

app.MapPost("/api/v1/risk/signal-intake/project", (
    SignalRiskEvaluationIntakeV1 intake,
    ISignalRiskProjector projector) =>
{
    var projection = projector.Project(intake);
    return projection.Outcome == SignalRiskEvaluationContractV1.Eligible
        ? Results.Ok(projection)
        : Results.UnprocessableEntity(projection);
});

if (persistenceOptions.UseSqlServer)
{
    app.MapPost("/api/v1/risk/signal-intake/enqueue", async (
        SignalRiskEvaluationIntakeV1 intake,
        ISignalRiskWorkQueue queue,
        CancellationToken cancellationToken) =>
    {
        var result = await queue.EnqueueAsync(intake, cancellationToken);
        return result.Outcome == "ENQUEUED"
            ? Results.Accepted($"/api/v1/risk/work-items/{result.MessageUid}", result)
            : Results.Ok(result);
    });
    app.MapPost("/api/v1/trade-plans/automatic/enqueue", async (
        AutomaticTradePlanIntakeV1 intake,
        IAutomaticTradePlanWorkQueue queue,
        CancellationToken cancellationToken) =>
    {
        var result = await queue.EnqueueAsync(intake, cancellationToken);
        return result.Outcome == "ENQUEUED"
            ? Results.Accepted($"/api/v1/trade-plans/work-items/{result.SourceMessageUid}", result)
            : result.Outcome == AutomaticTradePlanContractV1.Rejected
                ? Results.UnprocessableEntity(result)
                : Results.Ok(result);
    });
}

app.MapPost("/api/v1/risk/signal-intake/evaluate", (
    SignalRiskEvaluationIntakeV1 intake,
    ISignalRiskProjector projector,
    SignalRiskCoordinator coordinator) =>
{
    var projection = projector.Project(intake);
    return projection.Command is null
        ? Results.UnprocessableEntity(projection)
        : Results.Ok(coordinator.Evaluate(projection.Command));
});

app.MapPost("/api/v1/risk/evaluate", (RiskDecisionRequestV1 request, IRiskDecisionEngine engine) =>
{
    var decision = engine.Evaluate(request);
    return decision.Decision == RiskDecisionContractV1.Approved
        ? Results.Ok(decision)
        : Results.UnprocessableEntity(decision);
});

app.MapPost("/api/v1/trade-plans/automatic/project", (
    AutomaticTradePlanIntakeV1 intake,
    IAutomaticTradePlanProjector projector) =>
{
    var projection = projector.Project(intake);
    return projection.Outcome == AutomaticTradePlanContractV1.Eligible
        ? Results.Ok(projection)
        : Results.UnprocessableEntity(projection);
});

app.MapPost("/api/v1/trade-plans/automatic/build", (
    AutomaticTradePlanIntakeV1 intake,
    AutomaticTradePlanCoordinator coordinator) =>
{
    var result = coordinator.Build(intake);
    return result.Status == AutomaticTradePlanLifecycleStatus.Ready
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});

app.MapGet("/api/v1/trade-plans/automatic/{riskDecisionUid:guid}/{messageUid:guid}", (
    Guid riskDecisionUid,
    Guid messageUid,
    IAutomaticTradePlanLifecycleStore store) => Results.Ok(store.Read(riskDecisionUid, messageUid)));

app.MapPost("/api/v1/trade-plans/build", (TradePlanBuildRequestV1 request, ITradePlanBuilder planBuilder) =>
{
    var result = planBuilder.Build(request);
    return result.Status == TradePlanContractV1.Ready
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});

app.Run();

public partial class Program
{
}
