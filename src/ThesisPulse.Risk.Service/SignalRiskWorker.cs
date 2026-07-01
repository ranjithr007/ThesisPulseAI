using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SignalRiskWorker(
    SignalRiskWorkerOptions options,
    ISignalRiskWorkQueue queue,
    ISignalRiskProjector projector,
    SignalRiskCoordinator coordinator,
    SignalRiskWorkerState state,
    ILogger<SignalRiskWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Signal Risk worker is disabled.");
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
                state.Leased(items.Count);

                foreach (var item in items)
                    await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogError(exception, "Signal Risk worker polling failed.");
            }

            await Task.Delay(pollDelay, stoppingToken);
        }
    }

    private async Task ProcessAsync(
        SignalRiskWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var projection = projector.Project(item.Intake);
            if (projection.Command is null)
            {
                var reason = string.Join(',', projection.Reasons);
                if (projection.Reasons.Contains("SIGNAL_EXPIRED", StringComparer.Ordinal))
                {
                    await queue.ExpireAsync(item.WorkItemId, reason, cancellationToken);
                    state.Expired();
                }
                else
                {
                    await queue.FailAsync(item.WorkItemId, reason, cancellationToken);
                    state.Failed();
                }
                return;
            }

            var result = coordinator.Evaluate(projection.Command);
            await queue.CompleteAsync(item.WorkItemId, cancellationToken);
            state.Completed(string.Equals(result.Outcome, "DUPLICATE", StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            if (item.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(item.WorkItemId, exception.Message, cancellationToken);
                state.Failed();
            }
            else
            {
                var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(item.AttemptCount - 1, 6)));
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Signal Risk work item {WorkItemId} failed on attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }
}
