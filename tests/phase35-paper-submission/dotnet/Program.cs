using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

var failures = new List<string>();

Run("submission identity is replay stable", () =>
{
    var orderUid = Guid.NewGuid();
    Equal(
        AutomaticPaperSubmissionIdentity.SubmitEventUid(orderUid),
        AutomaticPaperSubmissionIdentity.SubmitEventUid(orderUid));
    Equal(
        AutomaticPaperSubmissionIdentity.AcknowledgeEventUid(orderUid),
        AutomaticPaperSubmissionIdentity.AcknowledgeEventUid(orderUid));
    Equal(
        AutomaticPaperSubmissionIdentity.BrokerOrderId(orderUid),
        AutomaticPaperSubmissionIdentity.BrokerOrderId(orderUid));
});

await RunAsync("healthy CREATED order becomes ACKNOWLEDGED without a fill", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var state = new AutomaticPaperSubmissionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        fixture.Service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.Acknowledged, queue.LastStatus!);
    var order = fixture.Service.GetOrder(fixture.Order.PaperOrderUid)
        ?? throw new InvalidOperationException("Acknowledged PAPER order was not persisted.");
    Equal(PaperOrderStateContractV1.Acknowledged, order.State);
    Equal(fixture.Item.BrokerOrderId, order.BrokerOrderId!);
    Equal(0m, order.FilledQuantity);
    Equal(order.RequestedQuantity, order.RemainingQuantity);
    Equal(1L, state.Snapshot().Acknowledged);
});

await RunAsync("kill switch fails closed and schedules retry", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var operations = HealthyOperations() with { KillSwitchActive = true };
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(operations),
        fixture.Service,
        new AutomaticPaperSubmissionWorkerState(),
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.RetryPending, queue.LastStatus!);
    Contains("KILL_SWITCH_ACTIVE", queue.Reasons);
    Equal(
        PaperOrderStateContractV1.Created,
        fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
});

await RunAsync("stale operational state fails closed", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var stale = HealthyOperations() with
    {
        ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
    };
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(stale, preserveObservedAt: true),
        fixture.Service,
        new AutomaticPaperSubmissionWorkerState(),
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.RetryPending, queue.LastStatus!);
    Contains("OPERATIONAL_SNAPSHOT_STALE", queue.Reasons);
});

await RunAsync("expired command expires the unsubmitted order", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        fixture.Service,
        new AutomaticPaperSubmissionWorkerState(),
        maximumAttempts: 3);
    var expired = fixture.Item with
    {
        ValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
    };

    await processor.ProcessAsync(expired, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.Expired, queue.LastStatus!);
    Equal(
        PaperOrderStateContractV1.Expired,
        fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
});

await RunAsync("submitted crash recovery acknowledges without rechecking market gates", async () =>
{
    var fixture = CreateFixture();
    var submitted = fixture.Service.ApplyEvent(
        fixture.Order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            fixture.Item.SubmitEventUid,
            PaperOrderEventContractV1.Submit,
            null,
            null,
            "SIMULATED_CRASH_AFTER_SUBMIT",
            DateTimeOffset.UtcNow,
            fixture.Item.BrokerOrderId));
    Require(submitted.Applied);

    var context = new FixedContextProvider(
        HealthyOperations() with { KillSwitchActive = true });
    var queue = new RecordingQueue();
    var state = new AutomaticPaperSubmissionWorkerState();
    var processor = CreateProcessor(
        queue,
        context,
        fixture.Service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(
        fixture.Item with { AttemptCount = 2 },
        CancellationToken.None);

    Equal(0, context.Calls);
    Equal(AutomaticPaperSubmissionStatus.Acknowledged, queue.LastStatus!);
    Equal(
        PaperOrderStateContractV1.Acknowledged,
        fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!.State);
    Equal(1L, state.Snapshot().Recovered);
});

await RunAsync("acknowledged replay does not mutate order version", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var state = new AutomaticPaperSubmissionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        fixture.Service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);
    var first = fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!;
    await processor.ProcessAsync(
        fixture.Item with { AttemptCount = 2 },
        CancellationToken.None);
    var second = fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!;

    Equal(first.Version, second.Version);
    Equal(first.BrokerOrderId!, second.BrokerOrderId!);
    Equal(2L, state.Snapshot().Acknowledged);
    Equal(1L, state.Snapshot().Recovered);
});

await RunAsync("transient context failure schedules retry", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var state = new AutomaticPaperSubmissionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(new TimeoutException("operations unavailable")),
        fixture.Service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.RetryPending, queue.LastStatus!);
    Equal(1L, state.Snapshot().Retried);
});

await RunAsync("retry exhaustion fails terminally without a fill", async () =>
{
    var fixture = CreateFixture();
    var queue = new RecordingQueue();
    var state = new AutomaticPaperSubmissionWorkerState();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(new TimeoutException("operations unavailable")),
        fixture.Service,
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(
        fixture.Item with { AttemptCount = 3 },
        CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.Failed, queue.LastStatus!);
    Equal(1L, state.Snapshot().Failed);
    var order = fixture.Service.GetOrder(fixture.Order.PaperOrderUid)!;
    Equal(0m, order.FilledQuantity);
    Equal(PaperOrderStateContractV1.Created, order.State);
});

await RunAsync("illegal source state is rejected", async () =>
{
    var fixture = CreateFixture();
    var cancelled = fixture.Service.ApplyEvent(
        fixture.Order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Cancel,
            null,
            null,
            "TEST_CANCEL",
            DateTimeOffset.UtcNow));
    Require(cancelled.Applied);

    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        new FixedContextProvider(HealthyOperations()),
        fixture.Service,
        new AutomaticPaperSubmissionWorkerState(),
        maximumAttempts: 3);

    await processor.ProcessAsync(fixture.Item, CancellationToken.None);

    Equal(AutomaticPaperSubmissionStatus.Rejected, queue.LastStatus!);
    Contains("INVALID_AUTOMATIC_SUBMISSION_SOURCE_STATE", queue.Reasons);
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.5 automatic PAPER submission tests passed.");
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

static AutomaticPaperSubmissionProcessor CreateProcessor(
    RecordingQueue queue,
    IAutomaticPaperSubmissionContextProvider contextProvider,
    IPaperExecutionService executionService,
    AutomaticPaperSubmissionWorkerState state,
    int maximumAttempts) => new(
        new AutomaticPaperSubmissionOptions
        {
            Enabled = true,
            PollIntervalSeconds = 5,
            BatchSize = 10,
            MaximumAttempts = maximumAttempts,
            MaximumOperationalSnapshotAgeSeconds = 30,
            OperationsServiceBaseUrl = "http://localhost:59485",
            InternalApiKey = "acceptance-key",
            TimeoutSeconds = 10,
        },
        queue,
        contextProvider,
        executionService,
        state,
        NullLogger<AutomaticPaperSubmissionProcessor>.Instance);

static SubmissionFixture CreateFixture()
{
    var service = CreateService();
    var now = DateTimeOffset.UtcNow;
    var plan = CreatePlan(now);
    var result = service.Authorize(new ExecutionCommandRequestV1(
        Guid.NewGuid(),
        $"phase35:{plan.TradePlanUid:N}",
        plan.CorrelationId,
        plan,
        HealthyOperations(),
        plan.ExecutionPolicyVersion,
        now));
    Equal(ExecutionCommandContractV1.Authorized, result.Status);
    var command = result.Command
        ?? throw new InvalidOperationException("Authorized result requires a command.");
    var order = result.PaperOrder
        ?? throw new InvalidOperationException("Authorized result requires a PAPER order.");
    var item = new AutomaticPaperSubmissionWorkItem(
        1001,
        2001,
        order.PaperOrderUid,
        3001,
        command.ExecutionCommandUid,
        order.CorrelationId,
        command.ValidUntilUtc,
        AutomaticPaperSubmissionIdentity.SubmitEventUid(order.PaperOrderUid),
        AutomaticPaperSubmissionIdentity.AcknowledgeEventUid(order.PaperOrderUid),
        AutomaticPaperSubmissionIdentity.ExpireEventUid(order.PaperOrderUid),
        AutomaticPaperSubmissionIdentity.BrokerOrderId(order.PaperOrderUid),
        1);
    return new SubmissionFixture(service, order, item);
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
    return new PersistentPaperExecutionService(
        gate,
        new InMemoryPaperExecutionLedgerStore());
}

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

sealed record SubmissionFixture(
    IPaperExecutionService Service,
    PaperOrderSnapshotV1 Order,
    AutomaticPaperSubmissionWorkItem Item);

sealed class FixedContextProvider : IAutomaticPaperSubmissionContextProvider
{
    private readonly ExecutionOperationalStateV1? _state;
    private readonly Exception? _exception;
    private readonly bool _preserveObservedAt;

    public FixedContextProvider(
        ExecutionOperationalStateV1 state,
        bool preserveObservedAt = false)
    {
        _state = state;
        _preserveObservedAt = preserveObservedAt;
    }

    public FixedContextProvider(Exception exception) => _exception = exception;

    public int Calls { get; private set; }

    public Task<ExecutionOperationalStateV1> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (_exception is not null)
            throw _exception;
        return Task.FromResult(
            _preserveObservedAt
                ? _state!
                : _state! with { ObservedAtUtc = DateTimeOffset.UtcNow });
    }
}

sealed class RecordingQueue : IAutomaticPaperSubmissionWorkQueue
{
    public string? LastStatus { get; private set; }
    public IReadOnlyCollection<string> Reasons { get; private set; } = Array.Empty<string>();
    public DateTimeOffset? AvailableAtUtc { get; private set; }
    public string? BrokerOrderId { get; private set; }

    public Task<AutomaticPaperSubmissionEnqueueResult> EnqueueAsync(
        AutomaticPaperSubmissionCandidate candidate,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AutomaticPaperSubmissionEnqueueResult(
            "ENQUEUED",
            candidate.OrderUid,
            Array.Empty<string>()));

    public Task<IReadOnlyCollection<AutomaticPaperSubmissionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<AutomaticPaperSubmissionWorkItem>>(
            Array.Empty<AutomaticPaperSubmissionWorkItem>());

    public Task AcknowledgeAsync(
        long workItemId,
        string brokerOrderId,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperSubmissionStatus.Acknowledged;
        BrokerOrderId = brokerOrderId;
        return Task.CompletedTask;
    }

    public Task RetryAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperSubmissionStatus.RetryPending;
        Reasons = reasons;
        AvailableAtUtc = availableAtUtc;
        return Task.CompletedTask;
    }

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperSubmissionStatus.Rejected;
        Reasons = reasons;
        return Task.CompletedTask;
    }

    public Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperSubmissionStatus.Expired;
        Reasons = new[] { reason };
        return Task.CompletedTask;
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperSubmissionStatus.Failed;
        Reasons = new[] { error };
        return Task.CompletedTask;
    }
}
