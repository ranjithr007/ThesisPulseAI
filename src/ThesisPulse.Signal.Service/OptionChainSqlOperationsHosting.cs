using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ThesisPulse.Signal.Service;

public sealed class OptionChainSqlOperationsHostedService(
    OptionChainSqlRuntimeOptions sqlRuntime,
    OptionChainOperationsOptions operations,
    OptionChainDurableRolloutCoordinator coordinator,
    IOptionChainRolloutAuditStore auditStore,
    OptionChainScheduleLease scheduleLease,
    ILogger<OptionChainSqlOperationsHostedService> logger) : BackgroundService
{
    public bool Restored { get; private set; }
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

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
            if (!scheduleLease.TryAcquireGuardrail())
            {
                logger.LogWarning("Skipped rollout maintenance because the previous cycle is active.");
                continue;
            }

            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-operations.AuditRetentionDays);
                await auditStore.DeleteExpiredAsync(cutoff, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rollout maintenance cycle failed.");
            }
            finally
            {
                scheduleLease.ReleaseGuardrail();
            }
        }
    }
}

public sealed class OptionChainSqlOperationsHealthCheck(
    OptionChainSqlRuntimeOptions runtime,
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

        return Task.FromResult(HealthCheckResult.Healthy("Durable rollout state is restored."));
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
        services.AddSingleton<OptionChainSqlOperationsHostedService>();
        services.AddHostedService(provider => provider.GetRequiredService<OptionChainSqlOperationsHostedService>());
        services.AddHealthChecks().AddCheck<OptionChainSqlOperationsHealthCheck>("option-chain-sql-operations");
        return services;
    }
}
