namespace ThesisPulse.Risk.Service;

public sealed class AutomaticTradePlanWorker(
    AutomaticTradePlanWorkerOptions options,
    IAutomaticTradePlanWorkQueue queue,
    AutomaticTradePlanWorkProcessor processor,
    AutomaticTradePlanWorkerState state,
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
                state.Leased(items.Count);
                foreach (var item in items)
                    await processor.ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogError(exception, "Automatic Trade Plan worker polling failed.");
            }

            await Task.Delay(pollDelay, stoppingToken);
        }
    }
}
