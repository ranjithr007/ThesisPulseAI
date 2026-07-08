using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

var failures = new List<string>();

Run("automatic execution identity is replay stable", () =>
{
    var tradePlanUid = Guid.NewGuid();
    Equal(
        AutomaticPaperExecutionIdentity.RequestUid(tradePlanUid),
        AutomaticPaperExecutionIdentity.RequestUid(tradePlanUid));
    Equal(
        AutomaticPaperExecutionIdentity.IdempotencyKey(tradePlanUid),
        AutomaticPaperExecutionIdentity.IdempotencyKey(tradePlanUid));
});

await RunAsync("PLAN_READY creates one authoritative PAPER order", async () =>
{
    var queue = new RecordingQueue();
    var state = new AutomaticPaperExecutionWorkerState();
    var service = CreateService();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        service,
        state,
        maximumAttempts: 3);
    var item = CreateItem(CreatePlan(DateTimeOffset.UtcNow), attemptCount: 1);

    await processor.ProcessAsync(item, CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.Authorized, queue.LastStatus!);
    Equal(1, queue.CommandUids.Count);
    Equal(1, queue.OrderUids.Count);
    var order = service.GetOrder(queue.OrderUids[0])
        ?? throw new InvalidOperationException("Authorized PAPER order was not persisted.");
    Equal(PaperOrderStateContractV1.Created, order.State);
    Equal(1L, state.Snapshot().Authorized);
    Equal(0L, state.Snapshot().Recovered);
});

await RunAsync("kill switch rejects without creating an order", async () =>
{
    var queue = new RecordingQueue();
    var service = CreateService();
    var operations = HealthyOperations() with { KillSwitchActive = true };
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(operations),
        service,
        new AutomaticPaperExecutionWorkerState(),
        maximumAttempts: 3);

    await processor.ProcessAsync(
        CreateItem(CreatePlan(DateTimeOffset.UtcNow), attemptCount: 1),
        CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.Rejected, queue.LastStatus!);
    Contains("KILL_SWITCH_CLEAR", queue.Reasons);
    Equal(0, queue.OrderUids.Count);
});

await RunAsync("expired plan never requests operational context", async () =>
{
    var now = DateTimeOffset.UtcNow;
    var context = new FixedContextProvider(HealthyOperations());
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        context,
        CreateService(),
        new AutomaticPaperExecutionWorkerState(),
        maximumAttempts: 3);
    var plan = CreatePlan(now) with { ValidUntilUtc = now.AddSeconds(-1) };

    await processor.ProcessAsync(
        CreateItem(plan, attemptCount: 1),
        CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.Expired, queue.LastStatus!);
    Equal(0, context.Calls);
});

await RunAsync("transient operations failure schedules retry", async () =>
{
    var queue = new RecordingQueue();
    var state = new AutomaticPaperExecutionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(new TimeoutException("operations unavailable")),
        CreateService(),
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(
        CreateItem(CreatePlan(DateTimeOffset.UtcNow), attemptCount: 1),
        CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.RetryPending, queue.LastStatus!);
    Require(queue.AvailableAtUtc > DateTimeOffset.UtcNow.AddSeconds(-1));
    Equal(1L, state.Snapshot().Retried);
});

await RunAsync("retry exhaustion fails terminally", async () =>
{
    var queue = new RecordingQueue();
    var state = new AutomaticPaperExecutionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(new TimeoutException("operations unavailable")),
        CreateService(),
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(
        CreateItem(CreatePlan(DateTimeOffset.UtcNow), attemptCount: 3),
        CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.Failed, queue.LastStatus!);
    Equal(1L, state.Snapshot().Failed);
});

await RunAsync("unknown outcome replay resolves the original command and order", async () =>
{
    var plan = CreatePlan(DateTimeOffset.UtcNow);
    var queue = new RecordingQueue();
    var state = new AutomaticPaperExecutionWorkerState();
    var service = CreateService();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(CreateItem(plan, attemptCount: 1), CancellationToken.None);
    await processor.ProcessAsync(CreateItem(plan, attemptCount: 2), CancellationToken.None);

    Equal(2, queue.CommandUids.Count);
    Equal(queue.CommandUids[0], queue.CommandUids[1]);
    Equal(queue.OrderUids[0], queue.OrderUids[1]);
    Equal(2L, state.Snapshot().Authorized);
    Equal(1L, state.Snapshot().Recovered);
});

await RunAsync("upstream self-authorization fails closed", async () =>
{
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        CreateService(),
        new AutomaticPaperExecutionWorkerState(),
        maximumAttempts: 3);
    var plan = CreatePlan(DateTimeOffset.UtcNow) with { ExecutionAuthorized = true };

    await processor.ProcessAsync(CreateItem(plan, attemptCount: 1), CancellationToken.None);

    Equal(AutomaticPaperExecutionStatus.Rejected, queue.LastStatus!);
    Contains("UPSTREAM_EXECUTION_AUTHORITY_FORBIDDEN", queue.Reasons);
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.4 automatic PAPER execution tests passed.");
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

static AutomaticPaperExecutionProcessor CreateProcessor(
    RecordingQueue queue,
    IAutomaticPaperExecutionContextProvider contextProvider,
    IPaperExecutionService executionService,
    AutomaticPaperExecutionWorkerState state,
    int maximumAttempts) => new(
        new AutomaticPaperExecutionOptions
        {
            Enabled = true,
            PollIntervalSeconds = 5,
            BatchSize = 10,
            MaximumAttempts = maximumAttempts,
            OperationsServiceBaseUrl = "http://localhost:59485",
            InternalApiKey = "test-acceptance-key",
            TimeoutSeconds = 10,
        },
        queue,
        contextProvider,
        executionService,
        state,
        NullLogger<AutomaticPaperExecutionProcessor>.Instance);

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
    return new PersistentPaperExecutionService(
        gate,
        new InMemoryPaperExecutionLedgerStore());
}

static AutomaticPaperExecutionWorkItem CreateItem(
    TradePlanV1 plan,
    int attemptCount) => new(
        101,
        501,
        Guid.NewGuid(),
        AutomaticPaperExecutionIdentity.RequestUid(plan.TradePlanUid),
        plan.CorrelationId,
        AutomaticPaperExecutionIdentity.IdempotencyKey(plan.TradePlanUid),
        plan,
        attemptCount);

static TradePlanV1 CreatePlan(DateTimeOffset now)
{
    var correlationId = Guid.NewGuid().ToString("D");
    return new TradePlanV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        correlationId,
        ExecutionCommandContractV1.PaperEnvironment,
        "NSE_EQ|INE002A01018",
        EvidenceDirectionV1.Long,
        "BUY",
        "INTRADAY",
        new TradePlanEntryV1("MARKET", 100m, null, null, 99m, 101m),
        10m,
        1m,
        true,
        new TradePlanStopLossV1(95m, "STOP_MARKET", null, true),
        new[]
        {
            new TradePlanTargetV1(1, 110m, 0.5m),
            new TradePlanTargetV1(2, 115m, 0.5m),
        },
        0.001m,
        "DAY",
        new TradeSessionV1(
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddHours(5)),
        new ExitPolicyV1(true, true, true, true, "exit-policy-v1.0.0"),
        50m,
        200000m,
        2m,
        "execution-policy-v1.0.0",
        TradePlanContractV1.Ready,
        false,
        now.AddSeconds(-5),
        now.AddMinutes(2));
}

static ExecutionOperationalStateV1 HealthyOperations() => new(
    false,
    false,
    true,
    true,
    true,
    DateTimeOffset.UtcNow);

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
        throw new InvalidOperationException(
            $"Expected '{expected}' but received '{actual}'.");
}

sealed class FixedContextProvider : IAutomaticPaperExecutionContextProvider
{
    private readonly ExecutionOperationalStateV1? _state;
    private readonly Exception? _exception;

    public FixedContextProvider(ExecutionOperationalStateV1 state) => _state = state;
    public FixedContextProvider(Exception exception) => _exception = exception;

    public int Calls { get; private set; }

    public Task<ExecutionOperationalStateV1> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (_exception is not null)
            throw _exception;
        return Task.FromResult(_state! with { ObservedAtUtc = DateTimeOffset.UtcNow });
    }
}

sealed class RecordingQueue : IAutomaticPaperExecutionWorkQueue
{
    public string? LastStatus { get; private set; }
    public DateTimeOffset? AvailableAtUtc { get; private set; }
    public List<Guid> CommandUids { get; } = new();
    public List<Guid> OrderUids { get; } = new();
    public IReadOnlyCollection<string> Reasons { get; private set; } = Array.Empty<string>();

    public Task<AutomaticPaperExecutionEnqueueResult> EnqueueAsync(
        AutomaticPaperExecutionCandidate candidate,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AutomaticPaperExecutionEnqueueResult(
            "ENQUEUED",
            candidate.TradePlan.TradePlanUid,
            AutomaticPaperExecutionIdentity.RequestUid(candidate.TradePlan.TradePlanUid),
            Array.Empty<string>()));

    public Task<IReadOnlyCollection<AutomaticPaperExecutionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<AutomaticPaperExecutionWorkItem>>(
            Array.Empty<AutomaticPaperExecutionWorkItem>());

    public Task AuthorizeAsync(
        long workItemId,
        Guid executionCommandUid,
        Guid orderUid,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperExecutionStatus.Authorized;
        CommandUids.Add(executionCommandUid);
        OrderUids.Add(orderUid);
        return Task.CompletedTask;
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperExecutionStatus.RetryPending;
        AvailableAtUtc = availableAtUtc;
        return Task.CompletedTask;
    }

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperExecutionStatus.Rejected;
        Reasons = reasons;
        return Task.CompletedTask;
    }

    public Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperExecutionStatus.Expired;
        Reasons = new[] { reason };
        return Task.CompletedTask;
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperExecutionStatus.Failed;
        return Task.CompletedTask;
    }
}
