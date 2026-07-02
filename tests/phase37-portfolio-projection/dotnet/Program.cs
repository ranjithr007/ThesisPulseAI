using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Portfolio.Service;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Portfolio;

var failures = new List<string>();

Run("projection request identity is replay stable", () =>
{
    var fillUid = Guid.NewGuid();
    Equal(
        AutomaticPortfolioFillProjectionIdentity.RequestUid(fillUid, "v1"),
        AutomaticPortfolioFillProjectionIdentity.RequestUid(fillUid, "v1"));
    NotEqual(
        AutomaticPortfolioFillProjectionIdentity.RequestUid(fillUid, "v1"),
        AutomaticPortfolioFillProjectionIdentity.RequestUid(fillUid, "v2"));
});

Run("missing portfolio routing fails closed", () =>
{
    var candidate = new AutomaticPortfolioFillProjectionCandidate(
        1001,
        Guid.NewGuid(),
        null,
        null,
        Guid.NewGuid().ToString("D"),
        DateTimeOffset.UtcNow,
        new[] { "PAPER_PORTFOLIO_ROUTING_NOT_FOUND" });

    var reasons = AutomaticPortfolioFillProjectionCandidateValidator.Validate(candidate);

    Contains("PAPER_PORTFOLIO_ROUTING_REQUIRED", reasons);
    Contains("PAPER_PORTFOLIO_ROUTING_NOT_FOUND", reasons);
});

await RunAsync("BUY fill automatically posts position and cash exactly once", async () =>
{
    var fill = CreateFill("BUY", 10m, 100m, 1m, 1m);
    var store = new InMemoryPortfolioLedgerStore();
    store.RegisterFill(fill);
    var queue = new RecordingQueue();
    var state = new AutomaticPortfolioFillProjectionWorkerState();
    var processor = CreateProcessor(queue, store, state);
    var item = CreateWorkItem(fill, attemptCount: 1);

    await processor.ProcessAsync(item, CancellationToken.None);

    Equal(AutomaticPortfolioFillProjectionStatus.Projected, queue.LastStatus!);
    Equal(PortfolioLedgerContractV1.Projected, queue.ProjectionStatus!);
    var snapshot = await store.GetSnapshotAsync("PAPER-ALPHA", fill.FilledAtUtc);
    var position = snapshot?.Positions.Single()
        ?? throw new InvalidOperationException("Projected position is required.");
    Equal(EvidenceDirectionV1.Long, position.Direction);
    Equal(10m, position.Quantity);
    Equal(100m, position.AverageOpenPrice!.Value);
    Equal(-1002m, snapshot!.CashBalances.Single().SettledAmount);
    Equal(position.PositionUid, queue.PositionUid!.Value);
    Equal(1L, state.Snapshot().Projected);
});

await RunAsync("SELL fill posts short position and side-correct cash", async () =>
{
    var fill = CreateFill("SELL", 10m, 100m, 1m, 1m);
    var store = new InMemoryPortfolioLedgerStore();
    store.RegisterFill(fill);
    var queue = new RecordingQueue();
    var processor = CreateProcessor(
        queue,
        store,
        new AutomaticPortfolioFillProjectionWorkerState());

    await processor.ProcessAsync(CreateWorkItem(fill, 1), CancellationToken.None);

    var snapshot = await store.GetSnapshotAsync("PAPER-ALPHA", fill.FilledAtUtc);
    var position = snapshot?.Positions.Single()
        ?? throw new InvalidOperationException("Projected short position is required.");
    Equal(EvidenceDirectionV1.Short, position.Direction);
    Equal(10m, position.Quantity);
    Equal(998m, snapshot!.CashBalances.Single().SettledAmount);
});

await RunAsync("replay is DUPLICATE and does not change ledger versions", async () =>
{
    var fill = CreateFill("BUY", 5m, 200m, 0m, 0m);
    var store = new InMemoryPortfolioLedgerStore();
    store.RegisterFill(fill);
    var queue = new RecordingQueue();
    var state = new AutomaticPortfolioFillProjectionWorkerState();
    var processor = CreateProcessor(queue, store, state);
    var firstItem = CreateWorkItem(fill, 1);

    await processor.ProcessAsync(firstItem, CancellationToken.None);
    var first = await store.GetSnapshotAsync("PAPER-ALPHA", fill.FilledAtUtc);
    var firstPositionVersion = first!.Positions.Single().Version;
    var firstCashVersion = first.CashBalances.Single().Version;

    await processor.ProcessAsync(firstItem with { AttemptCount = 2 }, CancellationToken.None);
    var second = await store.GetSnapshotAsync("PAPER-ALPHA", fill.FilledAtUtc.AddSeconds(1));

    Equal(AutomaticPortfolioFillProjectionStatus.Duplicate, queue.LastStatus!);
    Equal(firstPositionVersion, second!.Positions.Single().Version);
    Equal(firstCashVersion, second.CashBalances.Single().Version);
    Equal(1L, state.Snapshot().Projected);
    Equal(1L, state.Snapshot().Duplicates);
    Equal(1L, state.Snapshot().Recovered);
});

await RunAsync("ledger rejection becomes terminal queue rejection", async () =>
{
    var fill = CreateFill("BUY", 1m, 100m, 0m, 0m);
    var store = new InMemoryPortfolioLedgerStore();
    var queue = new RecordingQueue();
    var state = new AutomaticPortfolioFillProjectionWorkerState();
    var processor = CreateProcessor(queue, store, state);

    await processor.ProcessAsync(CreateWorkItem(fill, 1), CancellationToken.None);

    Equal(AutomaticPortfolioFillProjectionStatus.Rejected, queue.LastStatus!);
    Contains("FILL_NOT_FOUND", queue.Reasons);
    Equal(1L, state.Snapshot().Rejected);
});

await RunAsync("transient portfolio failure schedules bounded retry", async () =>
{
    var fill = CreateFill("BUY", 1m, 100m, 0m, 0m);
    var queue = new RecordingQueue();
    var state = new AutomaticPortfolioFillProjectionWorkerState();
    var processor = CreateProcessor(
        queue,
        new ThrowingPortfolioStore(new TimeoutException("portfolio unavailable")),
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(CreateWorkItem(fill, 1), CancellationToken.None);

    Equal(AutomaticPortfolioFillProjectionStatus.RetryPending, queue.LastStatus!);
    Equal(1L, state.Snapshot().Retried);
    Require(queue.AvailableAtUtc > DateTimeOffset.UtcNow);
});

await RunAsync("retry exhaustion fails without fabricating a ledger result", async () =>
{
    var fill = CreateFill("BUY", 1m, 100m, 0m, 0m);
    var queue = new RecordingQueue();
    var state = new AutomaticPortfolioFillProjectionWorkerState();
    var processor = CreateProcessor(
        queue,
        new ThrowingPortfolioStore(new TimeoutException("portfolio unavailable")),
        state,
        maximumAttempts: 3);

    await processor.ProcessAsync(CreateWorkItem(fill, 3), CancellationToken.None);

    Equal(AutomaticPortfolioFillProjectionStatus.Failed, queue.LastStatus!);
    Equal(1L, state.Snapshot().Failed);
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.7 automatic portfolio projection tests passed.");
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

static AutomaticPortfolioFillProjectionProcessor CreateProcessor(
    RecordingQueue queue,
    IPortfolioLedgerStore store,
    AutomaticPortfolioFillProjectionWorkerState state,
    int maximumAttempts = 5) => new(
        new AutomaticPortfolioFillProjectionOptions
        {
            Enabled = true,
            PollIntervalSeconds = 5,
            BatchSize = 10,
            MaximumAttempts = maximumAttempts,
            ProjectionPolicyVersion = "automatic-paper-fill-portfolio-projection-v1.0.0",
        },
        queue,
        store,
        state,
        NullLogger<AutomaticPortfolioFillProjectionProcessor>.Instance);

static PortfolioFillRecord CreateFill(
    string side,
    decimal quantity,
    decimal price,
    decimal fees,
    decimal taxes) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "PAPER",
        "NSE|RELIANCE",
        "INTRADAY",
        side,
        quantity,
        price,
        fees,
        taxes,
        "INR",
        DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString("D"));

static AutomaticPortfolioFillProjectionWorkItem CreateWorkItem(
    PortfolioFillRecord fill,
    int attemptCount) => new(
        1001,
        2001,
        fill.FillUid,
        3001,
        "PAPER-ALPHA",
        AutomaticPortfolioFillProjectionIdentity.RequestUid(
            fill.FillUid,
            "automatic-paper-fill-portfolio-projection-v1.0.0"),
        fill.CorrelationId,
        fill.FilledAtUtc,
        attemptCount);

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

sealed class RecordingQueue : IAutomaticPortfolioFillProjectionWorkQueue
{
    public string? LastStatus { get; private set; }
    public string? ProjectionStatus { get; private set; }
    public Guid? PositionUid { get; private set; }
    public IReadOnlyCollection<string> Reasons { get; private set; } = Array.Empty<string>();
    public DateTimeOffset? AvailableAtUtc { get; private set; }

    public Task<AutomaticPortfolioFillProjectionEnqueueResult> EnqueueAsync(
        AutomaticPortfolioFillProjectionCandidate candidate,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AutomaticPortfolioFillProjectionEnqueueResult(
            "ENQUEUED",
            candidate.FillUid,
            Array.Empty<string>()));

    public Task<IReadOnlyCollection<AutomaticPortfolioFillProjectionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<AutomaticPortfolioFillProjectionWorkItem>>(
            Array.Empty<AutomaticPortfolioFillProjectionWorkItem>());

    public Task CompleteAsync(
        long workItemId,
        string projectionStatus,
        Guid? positionUid,
        CancellationToken cancellationToken)
    {
        LastStatus = projectionStatus;
        ProjectionStatus = projectionStatus;
        PositionUid = positionUid;
        Reasons = Array.Empty<string>();
        return Task.CompletedTask;
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPortfolioFillProjectionStatus.RetryPending;
        Reasons = new[] { error };
        AvailableAtUtc = availableAtUtc;
        return Task.CompletedTask;
    }

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPortfolioFillProjectionStatus.Rejected;
        Reasons = reasons;
        return Task.CompletedTask;
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPortfolioFillProjectionStatus.Failed;
        Reasons = new[] { error };
        return Task.CompletedTask;
    }
}

sealed class ThrowingPortfolioStore(Exception exception) : IPortfolioLedgerStore
{
    public Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<PortfolioFillProjectionResultV1>(exception);

    public Task<PortfolioLedgerSnapshotV1?> GetSnapshotAsync(
        string portfolioCode,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
