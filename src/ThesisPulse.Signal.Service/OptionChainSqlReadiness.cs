using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainSqlReadinessSnapshot(
    bool DatabaseReachable,
    bool MigrationReady,
    bool RolloutStateRestored,
    bool SchedulerHeartbeatFresh,
    DateTimeOffset ObservedAtUtc,
    string Status,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed class OptionChainSqlReadinessProbe(
    IConfiguration configuration,
    OptionChainSqlRuntimeOptions runtime,
    OptionChainOperationsOptions operations,
    OptionChainSqlOperationsHostedService hosted)
{
    private string DatabaseConnection =>
        configuration["OptionChainSqlOperations:DatabaseConnection"]
        ?? throw new InvalidOperationException("Option-chain SQL database configuration is missing.");

    public async ValueTask<OptionChainSqlReadinessSnapshot> EvaluateAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Enabled)
            return new(true, true, true, true, DateTimeOffset.UtcNow, "DISABLED", false, false);

        var databaseReachable = false;
        var migrationReady = false;
        try
        {
            await using var connection = new SqlConnection(DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            databaseReachable = true;

            const string sql = """
                SELECT CASE WHEN
                    OBJECT_ID('intelligence.option_chain_rollout_audit', 'U') IS NOT NULL AND
                    OBJECT_ID('intelligence.option_chain_scheduler_leases', 'U') IS NOT NULL AND
                    OBJECT_ID('intelligence.option_chain_scheduler_runs', 'U') IS NOT NULL
                THEN 1 ELSE 0 END;
                """;
            await using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = runtime.CommandTimeoutSeconds
            };
            migrationReady = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch
        {
            databaseReachable = false;
            migrationReady = false;
        }

        var restored = hosted.Restored;
        var heartbeatFresh = !operations.SchedulerEnabled ||
            hosted.LastHeartbeatUtc is { } heartbeat &&
            DateTimeOffset.UtcNow - heartbeat <= TimeSpan.FromSeconds(operations.GuardrailIntervalSeconds * 3L);

        var ready = databaseReachable && migrationReady && restored && heartbeatFresh;
        return new(
            databaseReachable,
            migrationReady,
            restored,
            heartbeatFresh,
            DateTimeOffset.UtcNow,
            ready ? "READY" : "NOT_READY",
            false,
            false);
    }
}

public sealed class OptionChainFailClosedReadinessHealthCheck(
    OptionChainSqlReadinessProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await probe.EvaluateAsync(cancellationToken);
        return snapshot.Status == "READY" || snapshot.Status == "DISABLED"
            ? HealthCheckResult.Healthy(snapshot.Status, new Dictionary<string, object>
            {
                ["databaseReachable"] = snapshot.DatabaseReachable,
                ["migrationReady"] = snapshot.MigrationReady,
                ["rolloutStateRestored"] = snapshot.RolloutStateRestored,
                ["schedulerHeartbeatFresh"] = snapshot.SchedulerHeartbeatFresh,
                ["selectionAuthority"] = false,
                ["executionAuthority"] = false
            })
            : HealthCheckResult.Unhealthy("Option-chain SQL operations are not ready.", data: new Dictionary<string, object>
            {
                ["databaseReachable"] = snapshot.DatabaseReachable,
                ["migrationReady"] = snapshot.MigrationReady,
                ["rolloutStateRestored"] = snapshot.RolloutStateRestored,
                ["schedulerHeartbeatFresh"] = snapshot.SchedulerHeartbeatFresh,
                ["selectionAuthority"] = false,
                ["executionAuthority"] = false
            });
    }
}

public static class OptionChainFailClosedReadinessRegistration
{
    public static IServiceCollection AddOptionChainFailClosedReadiness(this IServiceCollection services)
    {
        services.AddSingleton<OptionChainSqlReadinessProbe>();
        services.AddHealthChecks().AddCheck<OptionChainFailClosedReadinessHealthCheck>(
            "option-chain-fail-closed-readiness");
        return services;
    }

    public static IEndpointRouteBuilder MapOptionChainFailClosedReadiness(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/operations/readiness", async (
            OptionChainSqlReadinessProbe probe,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await probe.EvaluateAsync(cancellationToken);
            return snapshot.Status == "READY" || snapshot.Status == "DISABLED"
                ? Results.Ok(snapshot)
                : Results.Json(snapshot, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        return endpoints;
    }
}
