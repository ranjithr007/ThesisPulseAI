using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed class AutomaticTradePlanWorker(
    AutomaticTradePlanWorkerOptions options,
    IAutomaticTradePlanWorkQueue queue,
    IAutomaticTradePlanProjector projector,
    ITradePlanBuilder builder,
    IAutomaticTradePlanResultStore resultStore,
    ILogger<AutomaticTradePlanWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:trade-plan";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Automatic Trade Plan worker is disabled.");
            return;
        }

        var pollDelay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(30, options.PollIntervalSeconds * 3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await queue.LeaseAsync(
                    options.BatchSize,
                    _leaseOwner,
                    leaseDuration,
                    stoppingToken);
                foreach (var item in items)
                    await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic Trade Plan worker polling failed.");
            }

            await Task.Delay(pollDelay, stoppingToken);
        }
    }

    private async Task ProcessAsync(
        AutomaticTradePlanWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var projection = projector.Project(item.Intake);
            if (projection.Command is null)
            {
                var reason = string.Join(',', projection.Reasons);
                if (projection.Reasons.Any(code => code is "SIGNAL_EXPIRED" or "RISK_BUDGET_EXPIRED_OR_MISSING"))
                    await queue.ExpireAsync(item.WorkItemId, reason, cancellationToken);
                else
                    await queue.RejectAsync(item.WorkItemId, reason, cancellationToken);
                return;
            }

            var buildResult = builder.Build(projection.Command.Request);
            if (buildResult.Status != TradePlanContractV1.Ready || buildResult.TradePlan is null)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    string.Join(',', buildResult.Reasons),
                    cancellationToken);
                return;
            }

            await resultStore.PersistReadyAsync(projection.Command, buildResult, cancellationToken);
            await queue.CompleteAsync(item.WorkItemId, cancellationToken);
        }
        catch (Exception exception)
        {
            if (item.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(item.WorkItemId, exception.Message, cancellationToken);
            }
            else
            {
                var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(item.AttemptCount - 1, 6)));
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                    cancellationToken);
            }

            logger.LogWarning(
                exception,
                "Automatic Trade Plan work item {WorkItemId} failed on attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }
}
