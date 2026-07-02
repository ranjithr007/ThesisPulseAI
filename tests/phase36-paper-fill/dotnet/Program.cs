using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

var failures = new List<string>();

Run("fill identity is deterministic per order candle and policy", () =>
{
    var orderUid = Guid.NewGuid();
    var candleUid = Guid.NewGuid();
    Equal(
        AutomaticPaperFillIdentity.FillEventUid(orderUid, candleUid, "v1"),
        AutomaticPaperFillIdentity.FillEventUid(orderUid, candleUid, "v1"));
    NotEqual(
        AutomaticPaperFillIdentity.FillEventUid(orderUid, candleUid, "v1"),
        AutomaticPaperFillIdentity.FillEventUid(orderUid, candleUid, "v2"));
});

await RunAsync("MARKET BUY fills full quantity with adverse bounded slippage", async () =>
{
    var fixture = CreateFixture("MARKET", null, null, "DAY", 0.001m, 99m, 102m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 101m, 99m, 100.5m);
    var queue = new RecordingQueue();
    var state = new AutomaticPaperFillWorkerState();
    var processor = CreateProcessor(queue, new FixedMarketDataProvider(candle), fixture.Service, state);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Filled, queue.LastStatus!);
    Equal(100.1m, queue.FillPrice!.Value);
    var order = fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!;
    Equal(PaperOrderStateContractV1.Filled, order.State);
    Equal(order.RequestedQuantity, order.FilledQuantity);
    Equal(0m, order.RemainingQuantity);
    Equal(100.1m, order.AverageFillPrice!.Value);
    Equal(1L, state.Snapshot().Filled);
});

await RunAsync("LIMIT BUY fills at better candle open", async () =>
{
    var fixture = CreateFixture("LIMIT", 100m, null, "DAY", 0m, 95m, 105m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 99m, 101m, 98m, 100m);
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(candle),
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Filled, queue.LastStatus!);
    Equal(99m, queue.FillPrice!.Value);
});

await RunAsync("LIMIT order without a touch is deferred", async () =>
{
    var fixture = CreateFixture("LIMIT", 95m, null, "DAY", 0m, 90m, 105m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 102m, 99m, 101m);
    var queue = new RecordingQueue();
    var state = new AutomaticPaperFillWorkerState();
    var processor = CreateProcessor(queue, new FixedMarketDataProvider(candle), fixture.Service, state);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Deferred, queue.LastStatus!);
    Equal(candle.CandleUid, queue.LastEvaluatedCandle!.CandleUid);
    Equal(PaperOrderStateContractV1.Acknowledged, fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
    Equal(1L, state.Snapshot().Deferred);
});

await RunAsync("STOP_MARKET SELL fills after trigger with adverse slippage", async () =>
{
    var fixture = CreateFixture(
        "STOP_MARKET",
        null,
        99m,
        "DAY",
        0.001m,
        95m,
        102m,
        EvidenceDirectionV1.Short);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 98m, 100m, 97m, 98.5m);
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(candle),
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Filled, queue.LastStatus!);
    Equal(97.902m, queue.FillPrice!.Value);
});

await RunAsync("STOP_LIMIT is rejected because intrabar ordering is unknowable", async () =>
{
    var fixture = CreateFixture("STOP_LIMIT", 101m, 100m, "DAY", 0m, 95m, 105m);
    var queue = new RecordingQueue();
    var provider = new FixedMarketDataProvider(
        CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 102m, 99m, 101m));
    var processor = CreateProcessor(
        queue,
        provider,
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Rejected, queue.LastStatus!);
    Contains("STOP_LIMIT_INTRABAR_ORDERING_UNPROVABLE", queue.Reasons);
    Equal(0, provider.Calls);
});

await RunAsync("IOC no-touch order expires after first eligible candle", async () =>
{
    var fixture = CreateFixture("LIMIT", 95m, null, "IOC", 0m, 90m, 105m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 102m, 99m, 101m);
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(candle),
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Expired, queue.LastStatus!);
    Equal(PaperOrderStateContractV1.Expired, fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
    Contains("IOC_NOT_FILLED_ON_FIRST_ELIGIBLE_CANDLE", queue.Reasons);
});

await RunAsync("DAY order expires at new-entry cutoff without a candle", async () =>
{
    var fixture = CreateFixture("LIMIT", 95m, null, "DAY", 0m, 90m, 105m);
    var expiredCommand = fixture.Item.Command with
    {
        Session = fixture.Item.Command.Session with
        {
            NewEntryCutoffUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        },
    };
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(),
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(
        fixture.Item with { Command = expiredCommand },
        CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Expired, queue.LastStatus!);
    Contains("DAY_ORDER_NEW_ENTRY_CUTOFF_REACHED", queue.Reasons);
});

await RunAsync("fill outside accepted price band is rejected", async () =>
{
    var fixture = CreateFixture("MARKET", null, null, "DAY", 0.001m, 99m, 100m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 101m, 99m, 100m);
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(candle),
        fixture.Service,
        new AutomaticPaperFillWorkerState());

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Rejected, queue.LastStatus!);
    Contains("SIMULATED_FILL_OUTSIDE_ACCEPTABLE_PRICE_BAND", queue.Reasons);
    Equal(PaperOrderStateContractV1.Acknowledged, fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
});

await RunAsync("filled-order crash recovery completes without market-data replay", async () =>
{
    var fixture = CreateFixture("MARKET", null, null, "DAY", 0m, 95m, 105m);
    var candle = CreateCandle(fixture.Item.EligibleAfterUtc, 100m, 101m, 99m, 100m);
    var eventUid = AutomaticPaperFillIdentity.FillEventUid(
        fixture.Order.PaperOrderUid,
        candle.CandleUid,
        "deterministic-paper-fill-v1.0.0");
    var fill = fixture.Service.ApplyEvent(
        fixture.Order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            eventUid,
            PaperOrderEventContractV1.Fill,
            fixture.Order.RemainingQuantity,
            100m,
            "SIMULATED_CRASH_AFTER_FILL",
            candle.CloseAtUtc,
            fixture.Order.BrokerOrderId));
    Require(fill.Applied);

    var provider = new FixedMarketDataProvider(new TimeoutException("must not be called"));
    var queue = new RecordingQueue();
    var state = new AutomaticPaperFillWorkerState();
    var processor = CreateProcessor(queue, provider, fixture.Service, state);

    await processor.ProcessAsync(
        fixture.Item with { EvaluationCount = 2 },
        CancellationToken.None);

    Equal(0, provider.Calls);
    Equal(AutomaticPaperFillStatus.Filled, queue.LastStatus!);
    Equal(1L, state.Snapshot().Recovered);
});

await RunAsync("transient market-data failure retries", async () =>
{
    var fixture = CreateFixture("MARKET", null, null, "DAY", 0m, 95m, 105m);
    var queue = new RecordingQueue();
    var state = new AutomaticPaperFillWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(new TimeoutException("market data unavailable")),
        fixture.Service,
        state,
        maximumErrorAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperFillStatus.RetryPending, queue.LastStatus!);
    Equal(1L, state.Snapshot().Retried);
});

await RunAsync("error-attempt exhaustion fails without mutating order", async () =>
{
    var fixture = CreateFixture("MARKET", null, null, "DAY", 0m, 95m, 105m);
    var queue = new RecordingQueue();
    var state = new AutomaticPaperFillWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedMarketDataProvider(new TimeoutException("market data unavailable")),
        fixture.Service,
        state,
        maximumErrorAttempts: 3);

    await processor.ProcessAsync(
        fixture.Item with { ErrorCount = 2 },
        CancellationToken.None);

    Equal(AutomaticPaperFillStatus.Failed, queue.LastStatus!);
    Equal(PaperOrderStateContractV1.Acknowledged, fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
    Equal(1L, state.Snapshot().Failed);
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.6 deterministic PAPER fill tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static AutomaticPaperFillProcessor CreateProcessor(
    RecordingQueue queue,
    IAutomaticPaperFillMarketDataProvider marketData,
    IPaperExecutionService service,
    AutomaticPaperFillWorkerState state,
    int maximumErrorAttempts = 5) => new(
        new AutomaticPaperFillOptions
        {
            Enabled = true,
            PollIntervalSeconds = 5,
            BatchSize = 10,
            MaximumErrorAttempts = maximumErrorAttempts,
            MaximumCandlesPerEvaluation = 200,
            DeferSeconds = 5,
            FillPolicyVersion = "deterministic-paper-fill-v1.0.0",
            MarketDataServiceBaseUrl = "http://localhost:5101",
            MarketDataBrokerCode = "UPSTOX",
            TimeoutSeconds = 10,
        },
        queue,
        marketData,
        new DeterministicPaperFillPolicy(),
        service,
        state,
        NullLogger<AutomaticPaperFillProcessor>.Instance);

static FillFixture CreateFixture(
    string orderType,
    decimal? limitPrice,
    decimal? triggerPrice,
    string timeInForce,
    decimal slippage,
    decimal minimumAcceptablePrice,
    decimal maximumAcceptablePrice,
    EvidenceDirectionV1 direction = EvidenceDirectionV1.Long)
{
    var service = CreateService();
    var now = DateTimeOffset.UtcNow;
    var plan = CreatePlan(
        now,
        orderType,
        limitPrice,
        triggerPrice,
        timeInForce,
        slippage,
        minimumAcceptablePrice,
        maximumAcceptablePrice,
        direction);
    var authorization = service.Authorize(new ExecutionCommandRequestV1(
        Guid.NewGuid(),
        $"phase36:{plan.TradePlanUid:N}",
        plan.CorrelationId,
        plan,
        HealthyOperationsAt(now),
        plan.ExecutionPolicyVersion,
        now));
    if (authorization.Status != ExecutionCommandContractV1.Authorized)
    {
        throw new InvalidOperationException(
            $"Execution authorization failed: {string.Join(',', authorization.Reasons)}");
    }

    var command = authorization.Command!;
    var created = authorization.PaperOrder!;
    var brokerOrderId = $"PAPER-{created.PaperOrderUid:N}";
    var submitted = service.ApplyEvent(
        created.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Submit,
            null,
            null,
            "TEST_SUBMIT",
            now.AddMilliseconds(1),
            brokerOrderId));
    Require(submitted.Applied);
    var acknowledged = service.ApplyEvent(
        created.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Acknowledge,
            null,
            null,
            "TEST_ACKNOWLEDGE",
            now.AddMilliseconds(2),
            brokerOrderId));
    Require(acknowledged.Applied && acknowledged.PaperOrder is not null);
    var order = acknowledged.PaperOrder!;

    var item = new AutomaticPaperFillWorkItem(
        1001,
        2001,
        order.PaperOrderUid,
        3001,
        command.ExecutionCommandUid,
        order.CorrelationId,
        "NSE_EQ|INE002A01018",
        order.UpdatedAtUtc,
        command,
        null,
        null,
        null,
        1,
        0);
    return new FillFixture(service, order, item);
}

static IPaperExecutionService CreateService()
{
    var gate = new DeterministicPaperExecutionService(
        Options.Create(new DeterministicPaperExecutionOptions
        {
            GateVersion = "deterministic-paper-execution-v1.0.0",
            ExecutionPolicyVersion = "execution-policy-v1.0.0",
            AllowedEnvironments = [ExecutionCommandContractV1.PaperEnvironment],
            MaximumOperationalSnapshotAgeSeconds = 30,
            MaximumCommandValiditySeconds = 30,
        }));
    return new PersistentPaperExecutionService(gate, new InMemoryPaperExecutionLedgerStore());
}

static TradePlanV1 CreatePlan(
    DateTimeOffset now,
    string orderType,
    decimal? limitPrice,
    decimal? triggerPrice,
    string timeInForce,
    decimal slippage,
    decimal minimumAcceptablePrice,
    decimal maximumAcceptablePrice,
    EvidenceDirectionV1 direction)
{
    var side = direction == EvidenceDirectionV1.Long ? "BUY" : "SELL";
    return new TradePlanV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        ExecutionCommandContractV1.PaperEnvironment,
        "NSE_EQ|INE002A01018",
        direction,
        side,
        "INTRADAY",
        new TradePlanEntryV1(
            orderType,
            100m,
            limitPrice,
            triggerPrice,
            minimumAcceptablePrice,
            maximumAcceptablePrice),
        10m,
        10m,
        false,
        new TradePlanStopLossV1(
            direction == EvidenceDirectionV1.Long ? 95m : 105m,
            "STOP_MARKET",
            null,
            true),
        new[]
        {
            new TradePlanTargetV1(1, direction == EvidenceDirectionV1.Long ? 110m : 90m, 1m),
        },
        slippage,
        timeInForce,
        new TradeSessionV1(
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddHours(5)),
        new ExitPolicyV1(true, true, true, true, "exit-policy-v1.0.0"),
        50m,
        1000m,
        2m,
        "execution-policy-v1.0.0",
        TradePlanContractV1.Ready,
        false,
        now.AddSeconds(-5),
        now.AddMinutes(2));
}

static StoredCandleV1 CreateCandle(
    DateTimeOffset afterUtc,
    decimal open,
    decimal high,
    decimal low,
    decimal close)
{
    var openAt = afterUtc.AddSeconds(1);
    return new StoredCandleV1(
        Random.Shared.NextInt64(1, long.MaxValue),
        Guid.NewGuid(),
        "NSE_EQ|INE002A01018",
        "1m",
        openAt,
        openAt.AddMinutes(1),
        open,
        high,
        low,
        close,
        1000m,
        MarketDataQualityStatusV1.Valid,
        true,
        openAt.AddMinutes(1));
}

static ExecutionOperationalStateV1 HealthyOperationsAt(DateTimeOffset observedAtUtc) => new(
    false,
    false,
    true,
    true,
    true,
    observedAtUtc);

static void Contains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"Expected reason '{expected}'.");
}

static void Require(bool value)
{
    if (!value)
        throw new InvalidOperationException("Acceptance assertion failed.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
}

static void NotEqual<T>(T left, T right) where T : notnull
{
    if (EqualityComparer<T>.Default.Equals(left, right))
        throw new InvalidOperationException($"Expected different values but both were '{left}'.");
}

sealed record FillFixture(
    IPaperExecutionService Service,
    PaperOrderSnapshotV1 Order,
    AutomaticPaperFillWorkItem Item);

sealed class FixedMarketDataProvider : IAutomaticPaperFillMarketDataProvider
{
    private readonly IReadOnlyCollection<StoredCandleV1> _candles;
    private readonly Exception? _exception;

    public FixedMarketDataProvider(params StoredCandleV1[] candles) => _candles = candles;
    public FixedMarketDataProvider(Exception exception)
    {
        _exception = exception;
        _candles = Array.Empty<StoredCandleV1>();
    }

    public int Calls { get; private set; }

    public Task<IReadOnlyCollection<StoredCandleV1>> GetClosedCandlesAsync(
        string providerInstrumentKey,
        DateTimeOffset afterUtc,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (_exception is not null)
            throw _exception;
        return Task.FromResult<IReadOnlyCollection<StoredCandleV1>>(
            _candles
                .Where(candle => candle.CloseAtUtc > afterUtc)
                .OrderBy(candle => candle.OpenAtUtc)
                .Take(maximumCount)
                .ToArray());
    }
}

sealed class RecordingQueue : IAutomaticPaperFillWorkQueue
{
    public string? LastStatus { get; private set; }
    public IReadOnlyCollection<string> Reasons { get; private set; } = Array.Empty<string>();
    public decimal? FillPrice { get; private set; }
    public Guid? FillEventUid { get; private set; }
    public StoredCandleV1? LastEvaluatedCandle { get; private set; }

    public Task<AutomaticPaperFillEnqueueResult> EnqueueAsync(
        AutomaticPaperFillCandidate candidate,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AutomaticPaperFillEnqueueResult(
            "ENQUEUED",
            candidate.OrderUid,
            Array.Empty<string>()));

    public Task<IReadOnlyCollection<AutomaticPaperFillWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<AutomaticPaperFillWorkItem>>(
            Array.Empty<AutomaticPaperFillWorkItem>());

    public Task CompleteAsync(
        long workItemId,
        Guid? fillEventUid,
        decimal? fillPrice,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.Filled;
        FillEventUid = fillEventUid;
        FillPrice = fillPrice;
        return Task.CompletedTask;
    }

    public Task DeferAsync(
        long workItemId,
        StoredCandleV1? lastEvaluatedCandle,
        DateTimeOffset availableAtUtc,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.Deferred;
        LastEvaluatedCandle = lastEvaluatedCandle;
        Reasons = reasons;
        return Task.CompletedTask;
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.RetryPending;
        Reasons = new[] { error };
        return Task.CompletedTask;
    }

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.Rejected;
        Reasons = reasons;
        return Task.CompletedTask;
    }

    public Task ExpireAsync(
        long workItemId,
        Guid expireEventUid,
        string reason,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.Expired;
        Reasons = new[] { reason };
        return Task.CompletedTask;
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperFillStatus.Failed;
        Reasons = new[] { error };
        return Task.CompletedTask;
    }
}
