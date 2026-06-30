using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public sealed record MarketDataRecoveryHealthSnapshot(
    bool Enabled,
    string Status,
    DateTimeOffset? LastStartedAtUtc,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRunAtUtc,
    int SubscriptionCount,
    int GapsDetected,
    int RecoveryRequests,
    int CandlesAccepted,
    int CandlesDuplicated,
    int CandlesRejected,
    string? LastError,
    IReadOnlyCollection<string> Warnings);

public sealed class MarketDataRecoveryHealthState
{
    private readonly object _sync = new();
    private MarketDataRecoveryHealthSnapshot _snapshot;

    public MarketDataRecoveryHealthState(MarketDataRecoveryOptions options)
    {
        _snapshot = new MarketDataRecoveryHealthSnapshot(
            options.Enabled,
            options.Enabled ? "STOPPED" : "DISABLED",
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            Array.Empty<string>());
    }

    public MarketDataRecoveryHealthSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot with { Warnings = _snapshot.Warnings.ToArray() };
        }
    }

    public void Running(DateTimeOffset startedAtUtc)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "RUNNING",
                LastStartedAtUtc = startedAtUtc,
                NextRunAtUtc = null,
                LastError = null,
            };
        }
    }

    public void Completed(
        MarketDataRecoveryRunResult result,
        DateTimeOffset nextRunAtUtc)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "HEALTHY",
                LastCompletedAtUtc = result.CompletedAtUtc,
                NextRunAtUtc = nextRunAtUtc,
                SubscriptionCount = result.SubscriptionCount,
                GapsDetected = result.GapsDetected,
                RecoveryRequests = result.RecoveryRequests,
                CandlesAccepted = result.CandlesAccepted,
                CandlesDuplicated = result.CandlesDuplicated,
                CandlesRejected = result.CandlesRejected,
                LastError = null,
                Warnings = result.Warnings.ToArray(),
            };
        }
    }

    public void Failed(
        string error,
        DateTimeOffset completedAtUtc,
        DateTimeOffset nextRunAtUtc)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Status = "DEGRADED",
                LastCompletedAtUtc = completedAtUtc,
                NextRunAtUtc = nextRunAtUtc,
                LastError = error,
            };
        }
    }
}

public sealed class MarketDataRecoveryWorker(
    IMarketDataSubscriptionCatalog subscriptionCatalog,
    IMarketDataProvider marketDataProvider,
    IMarketDataStore marketDataStore,
    IMarketDataGapDetector gapDetector,
    IMarketDataRecoveryStateStore recoveryStateStore,
    MarketDataRecoveryOptions options,
    UpstoxLiveFeedOptions liveFeedOptions,
    MarketDataRecoveryHealthState healthState,
    TimeProvider timeProvider,
    ILogger<MarketDataRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAtUtc = timeProvider.GetUtcNow();
            healthState.Running(startedAtUtc);

            try
            {
                var result = await RunOnceAsync(startedAtUtc, stoppingToken);
                var nextRunAtUtc = timeProvider.GetUtcNow().AddSeconds(
                    options.PollIntervalSeconds);
                healthState.Completed(result, nextRunAtUtc);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var completedAtUtc = timeProvider.GetUtcNow();
                var nextRunAtUtc = completedAtUtc.AddSeconds(options.PollIntervalSeconds);
                healthState.Failed(exception.Message, completedAtUtc, nextRunAtUtc);
                logger.LogError(exception, "Market Data reconciliation run failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.PollIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<MarketDataRecoveryRunResult> RunOnceAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var plan = await subscriptionCatalog.GetPlanAsync(
            "UPSTOX",
            liveFeedOptions.Mode,
            cancellationToken);
        var gapsDetected = 0;
        var recoveryRequests = 0;
        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var warnings = new List<string>();

        foreach (var subscription in plan.Items.OrderBy(item => item.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await marketDataStore.GetLatestCandlesAsync(
                subscription.ProviderInstrumentKey,
                subscription.RecoveryTimeframe,
                maximumCount: 3,
                cancellationToken);
            var gap = gapDetector.Detect(
                subscription,
                latest,
                timeProvider.GetUtcNow());

            if (gap is null)
            {
                continue;
            }

            gapsDetected++;
            var correlationId = Guid.NewGuid().ToString("D");
            await recoveryStateStore.RecordDetectedAsync(
                gap,
                correlationId,
                cancellationToken);

            try
            {
                var request = BuildHistoricalRequest(gap, correlationId);
                var candles = await marketDataProvider.GetHistoricalCandlesAsync(
                    request,
                    cancellationToken);
                recoveryRequests++;
                var result = await marketDataStore.PersistHistoricalCandlesAsync(
                    request,
                    candles,
                    cancellationToken);
                accepted += result.Accepted;
                duplicates += result.Duplicates;
                rejected += result.Rejected;
                warnings.AddRange(result.Warnings.Select(warning =>
                    $"{subscription.ProviderInstrumentKey}: {warning}"));

                var afterRecovery = await marketDataStore.GetLatestCandlesAsync(
                    subscription.ProviderInstrumentKey,
                    subscription.RecoveryTimeframe,
                    maximumCount: 3,
                    cancellationToken);
                var remainingGap = gapDetector.Detect(
                    subscription,
                    afterRecovery,
                    timeProvider.GetUtcNow());

                if (remainingGap is null)
                {
                    await recoveryStateStore.RecordRecoveredAsync(
                        gap,
                        correlationId,
                        cancellationToken);
                }
                else
                {
                    var warning =
                        $"Recovery did not close the full {gap.Timeframe} gap for " +
                        $"{gap.ProviderInstrumentKey}.";
                    warnings.Add(warning);
                    await recoveryStateStore.RecordFailureAsync(
                        gap,
                        correlationId,
                        warning,
                        cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(
                    $"{gap.ProviderInstrumentKey}: {exception.Message}");
                await recoveryStateStore.RecordFailureAsync(
                    gap,
                    correlationId,
                    exception.Message,
                    cancellationToken);
            }
        }

        return new MarketDataRecoveryRunResult(
            startedAtUtc,
            timeProvider.GetUtcNow(),
            plan.Items.Count,
            gapsDetected,
            recoveryRequests,
            accepted,
            duplicates,
            rejected,
            warnings.Take(200).ToArray());
    }

    private HistoricalCandleRequestV1 BuildHistoricalRequest(
        MarketDataGap gap,
        string correlationId)
    {
        var fromDate = DateOnly.FromDateTime(gap.GapStartUtc.UtcDateTime);
        var toDate = DateOnly.FromDateTime(gap.GapEndUtc.UtcDateTime);
        if (toDate.DayNumber - fromDate.DayNumber > options.MaximumRecoveryDays)
        {
            fromDate = toDate.AddDays(-options.MaximumRecoveryDays);
        }

        return new HistoricalCandleRequestV1(
            gap.ProviderInstrumentKey,
            gap.Timeframe,
            fromDate,
            toDate,
            correlationId);
    }
}
