using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ThesisPulse.Signal.Service;

public sealed class OptionChainSqlOperationsHostedService(
    OptionChainSqlRuntimeOptions sqlRuntime,
    OptionChainOperationsOptions operations,
    OptionChainDurableRolloutCoordinator coordinator,
    IOptionChainRolloutAuditStore auditStore,
    IOptionChainSchedulerStore schedulerStore,
    ILogger<OptionChainSqlOperationsHostedService> logger) : BackgroundService
{
    private const string RetentionJob = "ROLLOUT_AUDIT_RETENTION";

    public bool Restored { get; private set; }
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }
    public DateTimeOffset? LastSuccessfulRunUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!sqlRuntime.Enabled) return;

        await coordinator.RestoreAsync(stoppingToken);
        Restored = true;
        LastHeartbeatUtc = DateTimeOffset.UtcNow;

        if (!operations.SchedulerEnabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(operations.GuardrailIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            LastHeartbeatUtc = DateTimeOffset.UtcNow;
            await ExecuteRetentionCycleAsync(stoppingToken);
        }
    }

    private async Task ExecuteRetentionCycleAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var lease = await schedulerStore.TryAcquireAsync(
            RetentionJob,
            sqlRuntime.InstanceName,
            TimeSpan.FromSeconds(sqlRuntime.LeaseSeconds),
            startedAtUtc,
            cancellationToken);

        if (lease is null)
        {
            await RecordAsync("SKIPPED_LEASE_HELD", startedAtUtc, DateTimeOffset.UtcNow, null, cancellationToken);
            logger.LogInformation("Skipped rollout retention because another instance holds the database lease.");
            return;
        }

        try
        {
            var cutoff = startedAtUtc.AddDays(-operations.AuditRetentionDays);
            var removed = await auditStore.DeleteExpiredAsync(cutoff, cancellationToken);
            LastSuccessfulRunUtc = DateTimeOffset.UtcNow;
            await RecordAsync("COMPLETED", startedAtUtc, LastSuccessfulRunUtc.Value, $"removed={removed}", cancellationToken);
        }
        catch (Exception ex)
        {
            await RecordAsync("FAILED", startedAtUtc, DateTimeOffset.UtcNow, ex.GetType().Name, cancellationToken);
            logger.LogError(ex, "Rollout retention cycle failed.");
        }
        finally
        {
            await schedulerStore.ReleaseAsync(lease, cancellationToken);
        }
    }

    private ValueTask RecordAsync(
        string outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? detail,
        CancellationToken cancellationToken) =>
        schedulerStore.RecordRunAsync(new(
            Guid.NewGuid(),
            RetentionJob,
            sqlRuntime.InstanceName,
            outcome,
            startedAtUtc,
            completedAtUtc,
            detail,
            false,
            false), cancellationToken);
}

public sealed class OptionChainSqlOperationsHealthCheck(
    OptionChainSqlRuntimeOptions runtime,
    OptionChainOperationsOptions operations,
    OptionChainSqlOperationsHostedService hosted) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!runtime.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("SQL rollout operations are disabled."));

        if (!hosted.Restored)
            return Task.FromResult(HealthCheckResult.Unhealthy("Durable rollout state has not been restored."));

        if (operations.SchedulerEnabled && hosted.LastHeartbeatUtc is { } heartbeat)
        {
            var maximumAge = TimeSpan.FromSeconds(operations.GuardrailIntervalSeconds * 3L);
            if (DateTimeOffset.UtcNow - heartbeat > maximumAge)
                return Task.FromResult(HealthCheckResult.Unhealthy("Rollout scheduler heartbeat is stale."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Durable rollout state and scheduler heartbeat are ready."));
    }
}

public static class OptionChainSqlOperationsRegistration
{
    public static IServiceCollection AddOptionChainSqlOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var runtime = configuration.GetSection("OptionChainSqlOperations").Get<OptionChainSqlRuntimeOptions>() ?? new();
        services.AddSingleton(runtime);

        if (!runtime.Enabled) return services;

        services.RemoveAll<IOptionChainRolloutAuditStore>();
        services.AddSingleton<IOptionChainRolloutAuditStore, SqlServerOptionChainRolloutAuditStore>();
        services.AddSingleton<IOptionChainSchedulerStore, SqlServerOptionChainSchedulerStore>();
        services.AddSingleton<OptionChainSqlOperationsHostedService>();
        services.AddHostedService(provider => provider.GetRequiredService<OptionChainSqlOperationsHostedService>());
        services.AddHealthChecks().AddCheck<OptionChainSqlOperationsHealthCheck>("option-chain-sql-operations");
        return services;
    }
}
