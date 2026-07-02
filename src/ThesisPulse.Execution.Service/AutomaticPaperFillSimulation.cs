using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Execution.Service;

public static class AutomaticPaperFillStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Filled = "FILLED";
    public const string Deferred = "DEFERRED";
    public const string RetryPending = "RETRY_PENDING";
    public const string Expired = "EXPIRED";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public static class PaperFillPolicyOutcome
{
    public const string Fill = "FILL";
    public const string NoFill = "NO_FILL";
    public const string Reject = "REJECT";
}

public sealed class AutomaticPaperFillOptions
{
    public const string SectionName = "AutomaticPaperFill";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumErrorAttempts { get; init; } = 5;
    public int MaximumCandlesPerEvaluation { get; init; } = 200;
    public int DeferSeconds { get; init; } = 5;
    public string FillPolicyVersion { get; init; } = "deterministic-paper-fill-v1.0.0";
    public string MarketDataServiceBaseUrl { get; init; } = "http://localhost:5101";
    public string MarketDataBrokerCode { get; init; } = "UPSTOX";
    public int TimeoutSeconds { get; init; } = 10;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPaperFill:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("AutomaticPaperFill:BatchSize must be between 1 and 500.");
        if (MaximumErrorAttempts is < 1 or > 20)
            throw new InvalidOperationException("AutomaticPaperFill:MaximumErrorAttempts must be between 1 and 20.");
        if (MaximumCandlesPerEvaluation is < 1 or > 5000)
            throw new InvalidOperationException("AutomaticPaperFill:MaximumCandlesPerEvaluation must be between 1 and 5000.");
        if (DeferSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPaperFill:DeferSeconds must be between 1 and 300.");
        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("AutomaticPaperFill:TimeoutSeconds must be between 1 and 120.");
        ArgumentException.ThrowIfNullOrWhiteSpace(FillPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(MarketDataBrokerCode);
        if (!Enabled)
            return;
        if (!Uri.TryCreate(MarketDataServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("AutomaticPaperFill:MarketDataServiceBaseUrl must be absolute.");
    }
}

public sealed record AutomaticPaperFillCandidate(
    long OrderId,
    Guid OrderUid,
    long ExecutionCommandId,
    Guid ExecutionCommandUid,
    string CorrelationId,
    string ProviderInstrumentKey,
    DateTimeOffset EligibleAfterUtc,
    ExecutionCommandV1 Command);

public sealed record AutomaticPaperFillWorkItem(
    long WorkItemId,
    long OrderId,
    Guid OrderUid,
    long ExecutionCommandId,
    Guid ExecutionCommandUid,
    string CorrelationId,
    string ProviderInstrumentKey,
    DateTimeOffset EligibleAfterUtc,
    ExecutionCommandV1 Command,
    long? LastEvaluatedCandleId,
    Guid? LastEvaluatedCandleUid,
    DateTimeOffset? LastEvaluatedCloseAtUtc,
    int EvaluationCount,
    int ErrorCount);

public sealed record AutomaticPaperFillEnqueueResult(
    string Outcome,
    Guid OrderUid,
    IReadOnlyCollection<string> Reasons);

public sealed record PaperFillPolicyDecision(
    string Outcome,
    decimal? FillPrice,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPaperFillCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPaperFillCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperFillWorkQueue
{
    Task<AutomaticPaperFillEnqueueResult> EnqueueAsync(
        AutomaticPaperFillCandidate candidate,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPaperFillWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long workItemId,
        Guid? fillEventUid,
        decimal? fillPrice,
        CancellationToken cancellationToken);

    Task DeferAsync(
        long workItemId,
        StoredCandleV1? lastEvaluatedCandle,
        DateTimeOffset availableAtUtc,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken);

    Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken);

    Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken);

    Task ExpireAsync(
        long workItemId,
        Guid expireEventUid,
        string reason,
        CancellationToken cancellationToken);

    Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperFillMarketDataProvider
{
    Task<IReadOnlyCollection<StoredCandleV1>> GetClosedCandlesAsync(
        string providerInstrumentKey,
        DateTimeOffset afterUtc,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed record MarketDataCandleResponse(
    string InstrumentKey,
    string Timeframe,
    IReadOnlyCollection<StoredCandleV1> Candles,
    int Count);

public sealed class HttpAutomaticPaperFillMarketDataProvider(HttpClient client)
    : IAutomaticPaperFillMarketDataProvider
{
    public async Task<IReadOnlyCollection<StoredCandleV1>> GetClosedCandlesAsync(
        string providerInstrumentKey,
        DateTimeOffset afterUtc,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var uri = $"/api/v1/candles?instrumentKey={Uri.EscapeDataString(providerInstrumentKey)}&timeframe=1m&limit={maximumCount}";
        var response = await client.GetFromJsonAsync<MarketDataCandleResponse>(
            uri,
            cancellationToken)
            ?? throw new InvalidOperationException("Market Data Service returned an empty candle response.");

        return response.Candles
            .Where(candle => candle.CloseAtUtc > afterUtc)
            .Where(candle => candle.IsUsableForNewExposure)
            .Where(candle => string.Equals(
                candle.QualityStatus,
                MarketDataQualityStatusV1.Valid,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(candle => candle.OpenAtUtc)
            .ThenBy(candle => candle.CandleId)
            .ToArray();
    }
}

public sealed class DeterministicPaperFillPolicy
{
    public PaperFillPolicyDecision Evaluate(
        ExecutionCommandV1 command,
        PaperOrderSnapshotV1 order,
        StoredCandleV1 candle)
    {
        var reasons = Validate(command, order, candle);
        if (reasons.Count > 0)
            return new PaperFillPolicyDecision(PaperFillPolicyOutcome.Reject, null, reasons);

        var orderType = command.Entry.OrderType.Trim().ToUpperInvariant();
        var side = command.Side.Trim().ToUpperInvariant();
        decimal? price = orderType switch
        {
            "MARKET" => MarketPrice(side, candle.OpenPrice, command.MaximumSlippageFraction),
            "LIMIT" => LimitPrice(side, command.Entry.LimitPrice, candle),
            "STOP_MARKET" => StopMarketPrice(
                side,
                command.Entry.TriggerPrice,
                command.MaximumSlippageFraction,
                candle),
            "STOP_LIMIT" => null,
            _ => null,
        };

        if (orderType == "STOP_LIMIT")
        {
            return new PaperFillPolicyDecision(
                PaperFillPolicyOutcome.Reject,
                null,
                new[] { "STOP_LIMIT_INTRABAR_ORDERING_UNPROVABLE" });
        }

        if (orderType is not "MARKET" and not "LIMIT" and not "STOP_MARKET")
        {
            return new PaperFillPolicyDecision(
                PaperFillPolicyOutcome.Reject,
                null,
                new[] { "ORDER_TYPE_NOT_SUPPORTED_BY_PAPER_FILL_POLICY" });
        }

        if (price is null)
            return new PaperFillPolicyDecision(PaperFillPolicyOutcome.NoFill, null, Array.Empty<string>());

        var rounded = Math.Round(price.Value, 8, MidpointRounding.AwayFromZero);
        if (rounded < command.Entry.MinimumAcceptablePrice ||
            rounded > command.Entry.MaximumAcceptablePrice)
        {
            return new PaperFillPolicyDecision(
                PaperFillPolicyOutcome.Reject,
                null,
                new[] { "SIMULATED_FILL_OUTSIDE_ACCEPTABLE_PRICE_BAND" });
        }

        return new PaperFillPolicyDecision(
            PaperFillPolicyOutcome.Fill,
            rounded,
            Array.Empty<string>());
    }

    private static IReadOnlyCollection<string> Validate(
        ExecutionCommandV1 command,
        PaperOrderSnapshotV1 order,
        StoredCandleV1 candle)
    {
        var reasons = new List<string>();
        if (!string.Equals(command.Environment, ExecutionCommandContractV1.PaperEnvironment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(order.Environment, ExecutionCommandContractV1.PaperEnvironment, StringComparison.OrdinalIgnoreCase))
            reasons.Add("PAPER_ENVIRONMENT_REQUIRED");
        if (order.State != PaperOrderStateContractV1.Acknowledged)
            reasons.Add("ACKNOWLEDGED_ORDER_REQUIRED");
        if (order.FilledQuantity != 0m || order.RemainingQuantity != order.RequestedQuantity)
            reasons.Add("UNFILLED_FULL_QUANTITY_ORDER_REQUIRED");
        if (command.Quantity != order.RequestedQuantity)
            reasons.Add("COMMAND_ORDER_QUANTITY_MISMATCH");
        if (candle.OpenPrice <= 0 || candle.HighPrice <= 0 || candle.LowPrice <= 0 || candle.ClosePrice <= 0 ||
            candle.HighPrice < Math.Max(candle.OpenPrice, candle.ClosePrice) ||
            candle.LowPrice > Math.Min(candle.OpenPrice, candle.ClosePrice) ||
            candle.HighPrice < candle.LowPrice)
            reasons.Add("CANDLE_OHLC_INVALID");
        if (!candle.IsUsableForNewExposure ||
            !string.Equals(candle.QualityStatus, MarketDataQualityStatusV1.Valid, StringComparison.OrdinalIgnoreCase))
            reasons.Add("CANDLE_NOT_USABLE_FOR_NEW_EXPOSURE");
        if (command.MaximumSlippageFraction < 0 || command.MaximumSlippageFraction > 0.1m)
            reasons.Add("MAXIMUM_SLIPPAGE_INVALID");
        if (command.Side is not "BUY" and not "SELL")
            reasons.Add("ORDER_SIDE_UNSUPPORTED");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static decimal MarketPrice(string side, decimal open, decimal slippage) =>
        side == "BUY"
            ? open * (1m + slippage)
            : open * (1m - slippage);

    private static decimal? LimitPrice(
        string side,
        decimal? limit,
        StoredCandleV1 candle)
    {
        if (limit is null || limit <= 0)
            return null;
        if (side == "BUY")
            return candle.LowPrice <= limit ? Math.Min(candle.OpenPrice, limit.Value) : null;
        return candle.HighPrice >= limit ? Math.Max(candle.OpenPrice, limit.Value) : null;
    }

    private static decimal? StopMarketPrice(
        string side,
        decimal? trigger,
        decimal slippage,
        StoredCandleV1 candle)
    {
        if (trigger is null || trigger <= 0)
            return null;
        if (side == "BUY")
        {
            if (candle.HighPrice < trigger)
                return null;
            return Math.Max(candle.OpenPrice, trigger.Value) * (1m + slippage);
        }

        if (candle.LowPrice > trigger)
            return null;
        return Math.Min(candle.OpenPrice, trigger.Value) * (1m - slippage);
    }
}

public sealed record AutomaticPaperFillWorkerSnapshot(
    long Leased,
    long Filled,
    long Recovered,
    long Deferred,
    long Retried,
    long Expired,
    long Rejected,
    long Failed);

public sealed class AutomaticPaperFillWorkerState
{
    private long _leased;
    private long _filled;
    private long _recovered;
    private long _deferred;
    private long _retried;
    private long _expired;
    private long _rejected;
    private long _failed;

    public void Leased(int count) => Interlocked.Add(ref _leased, count);
    public void Filled(bool recovered)
    {
        Interlocked.Increment(ref _filled);
        if (recovered)
            Interlocked.Increment(ref _recovered);
    }
    public void Deferred() => Interlocked.Increment(ref _deferred);
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Expired() => Interlocked.Increment(ref _expired);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPaperFillWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _filled),
        Interlocked.Read(ref _recovered),
        Interlocked.Read(ref _deferred),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPaperFillProcessor(
    AutomaticPaperFillOptions options,
    IAutomaticPaperFillWorkQueue queue,
    IAutomaticPaperFillMarketDataProvider marketData,
    DeterministicPaperFillPolicy policy,
    IPaperExecutionService executionService,
    AutomaticPaperFillWorkerState state,
    ILogger<AutomaticPaperFillProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticPaperFillWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = executionService.GetOrder(item.OrderUid);
            if (order is null)
            {
                await RejectAsync(item, new[] { "PAPER_ORDER_NOT_FOUND" }, cancellationToken);
                return;
            }

            if (order.State == PaperOrderStateContractV1.Filled)
            {
                await queue.CompleteAsync(item.WorkItemId, null, order.AverageFillPrice, cancellationToken);
                state.Filled(recovered: true);
                return;
            }

            if (order.State != PaperOrderStateContractV1.Acknowledged ||
                order.FilledQuantity != 0m ||
                order.RemainingQuantity != order.RequestedQuantity)
            {
                await RejectAsync(item, new[] { "ACKNOWLEDGED_UNFILLED_ORDER_REQUIRED" }, cancellationToken);
                return;
            }

            var orderType = item.Command.Entry.OrderType.Trim().ToUpperInvariant();
            if (orderType == "STOP_LIMIT")
            {
                await RejectAsync(item, new[] { "STOP_LIMIT_INTRABAR_ORDERING_UNPROVABLE" }, cancellationToken);
                return;
            }

            if (orderType is not "MARKET" and not "LIMIT" and not "STOP_MARKET")
            {
                await RejectAsync(item, new[] { "ORDER_TYPE_NOT_SUPPORTED_BY_PAPER_FILL_POLICY" }, cancellationToken);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var afterUtc = item.LastEvaluatedCloseAtUtc ?? item.EligibleAfterUtc;
            var candles = await marketData.GetClosedCandlesAsync(
                item.ProviderInstrumentKey,
                afterUtc,
                options.MaximumCandlesPerEvaluation,
                cancellationToken);

            StoredCandleV1? lastEvaluated = null;
            foreach (var candle in candles)
            {
                lastEvaluated = candle;
                var decision = policy.Evaluate(item.Command, order, candle);
                if (decision.Outcome == PaperFillPolicyOutcome.Reject)
                {
                    await RejectAsync(item, decision.Reasons, cancellationToken);
                    return;
                }

                if (decision.Outcome == PaperFillPolicyOutcome.Fill && decision.FillPrice is not null)
                {
                    var eventUid = AutomaticPaperFillIdentity.FillEventUid(
                        item.OrderUid,
                        candle.CandleUid,
                        options.FillPolicyVersion);
                    var result = executionService.ApplyEvent(
                        item.OrderUid,
                        new PaperOrderEventRequestV1(
                            eventUid,
                            PaperOrderEventContractV1.Fill,
                            order.RemainingQuantity,
                            decision.FillPrice,
                            $"{options.FillPolicyVersion}:CANDLE:{candle.CandleUid:D}",
                            candle.CloseAtUtc,
                            order.BrokerOrderId));
                    if (!result.Applied || result.PaperOrder is null)
                    {
                        await RejectAsync(
                            item,
                            result.Reasons.Count > 0
                                ? result.Reasons
                                : new[] { "PAPER_FILL_TRANSITION_REJECTED" },
                            cancellationToken);
                        return;
                    }

                    if (result.PaperOrder.State != PaperOrderStateContractV1.Filled ||
                        result.PaperOrder.RemainingQuantity != 0m ||
                        result.PaperOrder.FilledQuantity != result.PaperOrder.RequestedQuantity)
                    {
                        await RejectAsync(item, new[] { "AUTHORITATIVE_FILLED_ORDER_INVALID" }, cancellationToken);
                        return;
                    }

                    await queue.CompleteAsync(
                        item.WorkItemId,
                        eventUid,
                        decision.FillPrice,
                        cancellationToken);
                    state.Filled(result.IdempotentReplay || item.EvaluationCount > 1);
                    return;
                }

                if (string.Equals(item.Command.TimeInForce, "IOC", StringComparison.OrdinalIgnoreCase))
                {
                    await ExpireAsync(
                        item,
                        order,
                        candle.CloseAtUtc,
                        "IOC_NOT_FILLED_ON_FIRST_ELIGIBLE_CANDLE",
                        cancellationToken);
                    return;
                }
            }

            if (now >= item.Command.Session.NewEntryCutoffUtc)
            {
                await ExpireAsync(
                    item,
                    order,
                    now,
                    "DAY_ORDER_NEW_ENTRY_CUTOFF_REACHED",
                    cancellationToken);
                return;
            }

            await queue.DeferAsync(
                item.WorkItemId,
                lastEvaluated,
                now.AddSeconds(options.DeferSeconds),
                candles.Count == 0
                    ? new[] { "NO_NEW_ELIGIBLE_CANDLE" }
                    : new[] { "ORDER_NOT_TOUCHED" },
                cancellationToken);
            state.Deferred();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var nextErrorCount = item.ErrorCount + 1;
            if (nextErrorCount >= options.MaximumErrorAttempts)
            {
                await queue.FailAsync(item.WorkItemId, exception.Message, cancellationToken);
                state.Failed();
            }
            else
            {
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(BackoffSeconds(nextErrorCount)),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic PAPER fill work item {WorkItemId} failed at error attempt {ErrorAttempt}.",
                item.WorkItemId,
                nextErrorCount);
        }
    }

    private async Task RejectAsync(
        AutomaticPaperFillWorkItem item,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        await queue.RejectAsync(item.WorkItemId, reasons, cancellationToken);
        state.Rejected();
    }

    private async Task ExpireAsync(
        AutomaticPaperFillWorkItem item,
        PaperOrderSnapshotV1 order,
        DateTimeOffset occurredAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        var eventTime = occurredAtUtc < order.UpdatedAtUtc
            ? order.UpdatedAtUtc
            : occurredAtUtc;
        var eventUid = AutomaticPaperFillIdentity.ExpireEventUid(
            item.OrderUid,
            options.FillPolicyVersion);
        var result = executionService.ApplyEvent(
            item.OrderUid,
            new PaperOrderEventRequestV1(
                eventUid,
                PaperOrderEventContractV1.Expire,
                null,
                null,
                reason,
                eventTime,
                order.BrokerOrderId));
        if (!result.Applied)
        {
            await RejectAsync(item, result.Reasons, cancellationToken);
            return;
        }

        await queue.ExpireAsync(
            item.WorkItemId,
            eventUid,
            reason,
            cancellationToken);
        state.Expired();
    }

    private static int BackoffSeconds(int errorCount) =>
        Math.Min(300, 5 * (1 << Math.Min(Math.Max(errorCount - 1, 0), 6)));
}

public sealed class AutomaticPaperFillIntakeWorker(
    AutomaticPaperFillOptions options,
    IAutomaticPaperFillCandidateStore candidateStore,
    IAutomaticPaperFillWorkQueue queue,
    ILogger<AutomaticPaperFillIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await candidateStore.ReadPendingAsync(
                    options.BatchSize,
                    stoppingToken);
                foreach (var candidate in candidates)
                    await queue.EnqueueAsync(candidate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic PAPER fill discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public sealed class AutomaticPaperFillWorker(
    AutomaticPaperFillOptions options,
    IAutomaticPaperFillWorkQueue queue,
    AutomaticPaperFillProcessor processor,
    AutomaticPaperFillWorkerState state,
    ILogger<AutomaticPaperFillWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:paper-fill";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
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
                logger.LogError(exception, "Automatic PAPER fill worker polling failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public static class AutomaticPaperFillIdentity
{
    public static Guid FillEventUid(
        Guid orderUid,
        Guid candleUid,
        string policyVersion) =>
        DeterministicGuidV1.Create(
            orderUid,
            $"automatic-paper-fill:{policyVersion}:{candleUid:D}");

    public static Guid ExpireEventUid(Guid orderUid, string policyVersion) =>
        DeterministicGuidV1.Create(
            orderUid,
            $"automatic-paper-fill-expire:{policyVersion}");
}
