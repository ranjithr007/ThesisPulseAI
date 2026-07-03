namespace ThesisPulse.Signal.Service;

public sealed record OptionChainProductionReadinessReport(
    bool Ready,
    string ActivationMode,
    bool RuntimeEnabled,
    bool DiscoveryEnabled,
    bool ExecutionEnabled,
    bool SqlServerConfigured,
    bool PythonRuntimeConfigured,
    bool QueueRecoveryConfigured,
    bool OperationalSmokeEnabled,
    bool SelectionAuthority,
    bool ExecutionAuthority,
    IReadOnlyCollection<string> BlockingReasons,
    DateTimeOffset ObservedAtUtc);

public static class OptionChainProductionReadinessEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainProductionReadiness(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/production-readiness", (
            IConfiguration configuration,
            OptionChainRuntimeOptions runtime) =>
        {
            var report = Build(configuration, runtime, DateTimeOffset.UtcNow);
            return report.Ready ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        endpoints.MapPost("/api/v1/internal/option-chain/operational-smoke", (
            IConfiguration configuration,
            OptionChainRuntimeOptions runtime) =>
        {
            var report = Build(configuration, runtime, DateTimeOffset.UtcNow);
            if (!configuration.GetValue("ProductionActivation:OperationalSmokeEnabled", false))
                return Results.Conflict(new { outcome = "SMOKE_DISABLED", report });

            return report.Ready
                ? Results.Ok(new { outcome = "READY", report })
                : Results.Json(new { outcome = "BLOCKED", report }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return endpoints;
    }

    public static OptionChainProductionReadinessReport Build(
        IConfiguration configuration,
        OptionChainRuntimeOptions runtime,
        DateTimeOffset observedAtUtc)
    {
        var activationMode = configuration["ProductionActivation:Mode"] ?? "DISABLED";
        var controlledMode = string.Equals(activationMode, "CONTROLLED", StringComparison.OrdinalIgnoreCase);
        var smokeEnabled = configuration.GetValue("ProductionActivation:OperationalSmokeEnabled", false);
        var sqlConfigured = string.Equals(runtime.PersistenceProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(runtime.ConnectionString);
        var pythonConfigured = Uri.TryCreate(runtime.PythonServiceBaseUrl, UriKind.Absolute, out _)
            && !string.IsNullOrWhiteSpace(runtime.PythonInternalApiKey);
        var queueRecoveryConfigured = runtime.WorkerMaximumAttempts >= 2
            && runtime.WorkerLeaseSeconds >= 30
            && runtime.WorkerInitialRetrySeconds >= 1
            && runtime.WorkerMaximumRetrySeconds >= runtime.WorkerInitialRetrySeconds;

        var reasons = new List<string>();
        if (!controlledMode) reasons.Add("ACTIVATION_MODE_NOT_CONTROLLED");
        if (!runtime.Enabled) reasons.Add("OPTION_CHAIN_RUNTIME_DISABLED");
        if (!runtime.WorkerDiscoveryEnabled) reasons.Add("OPTION_CHAIN_DISCOVERY_DISABLED");
        if (!runtime.WorkerExecutionEnabled) reasons.Add("OPTION_CHAIN_EXECUTION_DISABLED");
        if (!sqlConfigured) reasons.Add("SQL_SERVER_NOT_CONFIGURED");
        if (!pythonConfigured) reasons.Add("PYTHON_RUNTIME_SECRET_OR_URL_MISSING");
        if (!queueRecoveryConfigured) reasons.Add("QUEUE_RECOVERY_POLICY_INVALID");
        if (!smokeEnabled) reasons.Add("OPERATIONAL_SMOKE_DISABLED");

        return new OptionChainProductionReadinessReport(
            reasons.Count == 0,
            activationMode,
            runtime.Enabled,
            runtime.WorkerDiscoveryEnabled,
            runtime.WorkerExecutionEnabled,
            sqlConfigured,
            pythonConfigured,
            queueRecoveryConfigured,
            smokeEnabled,
            SelectionAuthority: false,
            ExecutionAuthority: false,
            reasons,
            observedAtUtc);
    }
}
