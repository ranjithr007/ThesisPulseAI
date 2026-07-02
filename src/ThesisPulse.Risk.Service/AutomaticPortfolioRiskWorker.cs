namespace ThesisPulse.Risk.Service;

public sealed record AutomaticPortfolioRiskWorkerSnapshot(
    long Discovered,
    long Enqueued,
    long Duplicates,
    long Leased,
    long Evaluated,
    long Retried,
    long Failed);

public sealed class AutomaticPortfolioRiskWorkerState
{
    private long _discovered;
    private long _enqueued;
    private long _duplicates;
    private long _leased;
    private long _evaluated;
    private long _retried;
    private long _failed;

    public void Discovered(long count) => Interlocked.Add(ref _discovered, count);
    public void Enqueued() => Interlocked.Increment(ref _enqueued);
    public void Duplicate() => Interlocked.Increment(ref _duplicates);
    public void Leased(long count) => Interlocked.Add(ref _leased, count);
    public void Evaluated() => Interlocked.Increment(ref _evaluated);
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPortfolioRiskWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _discovered),
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _evaluated),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPortfolioRiskWorker(
    IAutomaticPortfolioRiskCandidateStore candidateStore,
    IAutomaticPortfolioRiskWorkQueue queue,
    AutomaticPortfolioRiskProcessor processor,
    AutomaticPortfolioRiskOptions options,
    AutomaticPortfolioRiskWorkerState state,
    ILogger<AutomaticPortfolioRiskWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(stoppingToken);
                await ProcessLeasedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic portfolio risk worker cycle failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(options.PollIntervalSeconds),
                stoppingToken);
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = await candidateStore.ReadPendingAsync(
            options.BatchSize,
            cancellationToken);
        state.Discovered(candidates.Count);

        foreach (var candidate in candidates)
        {
            var result = await queue.EnqueueAsync(candidate, cancellationToken);
            if (result.Outcome == "ENQUEUED")
                state.Enqueued();
            else if (result.Outcome == AutomaticPortfolioRiskStatus.Duplicate)
                state.Duplicate();
            else
                state.Failed();
        }
    }

    private async Task ProcessLeasedAsync(CancellationToken cancellationToken)
    {
        var workItems = await queue.LeaseAsync(
            options.BatchSize,
            _leaseOwner,
            TimeSpan.FromSeconds(Math.Max(30, options.PollIntervalSeconds * 6)),
            cancellationToken);
        state.Leased(workItems.Count);

        foreach (var workItem in workItems)
        {
            try
            {
                var outcome = await processor.ProcessAsync(workItem, cancellationToken);
                if (outcome == AutomaticPortfolioRiskStatus.Evaluated)
                    state.Evaluated();
                else if (outcome == AutomaticPortfolioRiskStatus.Duplicate)
                    state.Duplicate();
                else if (outcome == AutomaticPortfolioRiskStatus.RetryPending)
                    state.Retried();
                else
                    state.Failed();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogError(
                    exception,
                    "Portfolio risk work item {WorkItemId} failed outside processor handling.",
                    workItem.WorkItemId);
            }
        }
    }
}
