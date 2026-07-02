using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Portfolio.Service;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;

var failures = new List<string>();
var evaluatedAt = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);
var commonClose = evaluatedAt.AddSeconds(-30);

Run("common point values long and short positions", () =>
{
    var candidate = Candidate(
        null,
        Position(11, "LONG", 10m, 100m, 20m, 2m, 1m, "NSE_EQ|LONG"),
        Position(12, "SHORT", 5m, 200m, -5m, 1m, 1m, "NSE_EQ|SHORT"));
    var candles = new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>
    {
        [11] = [Candle(101, "NSE_EQ|LONG", commonClose, 110m)],
        [12] = [Candle(102, "NSE_EQ|SHORT", commonClose, 180m)],
    };

    var result = Policy().Evaluate(candidate, candles, evaluatedAt);

    Equal(PortfolioValuationContractV1.Valued, result.Outcome);
    Equal(commonClose, result.AsOfUtc!.Value);
    Equal(200m, result.UnrealizedPnlAmount);
    Equal(15m, result.RealizedPnlAmount);
    Equal(215m, result.NetPnlAmount);
    Equal(220m, result.GrossPnlAmount);
    Equal(2000m, result.GrossExposureAmount);
    Equal(200m, result.NetExposureAmount);
});

Run("mixed timestamps defer the entire portfolio", () =>
{
    var candidate = Candidate(
        null,
        Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, "NSE_EQ|LONG"),
        Position(12, "SHORT", 5m, 200m, 0m, 0m, 0m, "NSE_EQ|SHORT"));
    var candles = new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>
    {
        [11] = [Candle(101, "NSE_EQ|LONG", commonClose, 110m)],
        [12] = [Candle(102, "NSE_EQ|SHORT", commonClose.AddMinutes(-1), 180m)],
    };

    var result = Policy().Evaluate(candidate, candles, evaluatedAt);

    Equal(PortfolioValuationContractV1.Deferred, result.Outcome);
    Contains("COMMON_CLOSED_CANDLE_NOT_AVAILABLE", result.Reasons);
});

Run("stale and invalid evidence fails closed", () =>
{
    var candidate = Candidate(
        null,
        Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, "NSE_EQ|LONG"));
    var invalid = Candle(102, "NSE_EQ|LONG", commonClose, 110m) with
    {
        QualityStatus = MarketDataQualityStatusV1.Invalid,
        IsUsableForNewExposure = false,
    };
    var candles = new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>
    {
        [11] = [Candle(101, "NSE_EQ|LONG", evaluatedAt.AddMinutes(-10), 110m), invalid],
    };

    var result = Policy().Evaluate(candidate, candles, evaluatedAt);

    Equal(PortfolioValuationContractV1.Deferred, result.Outcome);
    Contains("VALID_FRESH_CANDLE_NOT_AVAILABLE", result.Reasons);
});

Run("existing point is duplicate and missing mapping rejects", () =>
{
    var duplicateCandidate = Candidate(
        commonClose,
        Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, "NSE_EQ|LONG"));
    var duplicate = Policy().Evaluate(
        duplicateCandidate,
        new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>
        {
            [11] = [Candle(101, "NSE_EQ|LONG", commonClose, 110m)],
        },
        evaluatedAt);
    Equal(PortfolioValuationContractV1.Duplicate, duplicate.Outcome);

    var rejected = Policy().Evaluate(
        Candidate(null, Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, null)),
        new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>(),
        evaluatedAt);
    Equal(PortfolioValuationContractV1.Rejected, rejected.Outcome);
    Contains("MARKET_DATA_INSTRUMENT_MAPPING_REQUIRED", rejected.Reasons);
});

Run("identity changes with point and position version", () =>
{
    var first = Candidate(
        null,
        Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, "NSE_EQ|LONG"));
    var changed = first with
    {
        Positions = first.Positions
            .Select(position => position with { PositionVersion = 2 })
            .ToArray(),
    };
    var firstUid = AutomaticPaperValuationIdentity.RequestUid(first, commonClose, "v1");

    NotEqual(firstUid, AutomaticPaperValuationIdentity.RequestUid(first, commonClose.AddMinutes(1), "v1"));
    NotEqual(firstUid, AutomaticPaperValuationIdentity.RequestUid(changed, commonClose, "v1"));
    Equal(
        AutomaticPaperValuationIdentity.SnapshotUid(firstUid),
        AutomaticPaperValuationIdentity.SnapshotUid(firstUid));
});

await RunAsync("processor records success duplicate retry and failure", async () =>
{
    var valuedItem = WorkItem(1);
    var valuedQueue = new RecordingQueue();
    var valuedState = new AutomaticPaperValuationWorkerState();
    await Processor(
        valuedQueue,
        new FixedLedger(PortfolioValuationContractV1.Valued),
        valuedState).ProcessAsync(valuedItem, CancellationToken.None);
    Equal(AutomaticPaperValuationStatus.Valued, valuedQueue.LastStatus!);
    Equal(1L, valuedState.Snapshot().Valued);

    var duplicateQueue = new RecordingQueue();
    var duplicateState = new AutomaticPaperValuationWorkerState();
    await Processor(
        duplicateQueue,
        new FixedLedger(PortfolioValuationContractV1.Duplicate),
        duplicateState).ProcessAsync(WorkItem(2), CancellationToken.None);
    Equal(AutomaticPaperValuationStatus.Duplicate, duplicateQueue.LastStatus!);
    Equal(1L, duplicateState.Snapshot().Recovered);

    var retryQueue = new RecordingQueue();
    var retryState = new AutomaticPaperValuationWorkerState();
    await Processor(
        retryQueue,
        new ThrowingLedger(new TimeoutException("unavailable")),
        retryState,
        3).ProcessAsync(WorkItem(1), CancellationToken.None);
    Equal(AutomaticPaperValuationStatus.RetryPending, retryQueue.LastStatus!);

    var failQueue = new RecordingQueue();
    var failState = new AutomaticPaperValuationWorkerState();
    await Processor(
        failQueue,
        new ThrowingLedger(new TimeoutException("unavailable")),
        failState,
        3).ProcessAsync(WorkItem(3), CancellationToken.None);
    Equal(AutomaticPaperValuationStatus.Failed, failQueue.LastStatus!);
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.8 simulation snapshot tests passed.");
return 0;

DeterministicPaperValuationPolicy Policy() => new(new AutomaticPaperValuationOptions
{
    MaximumCandleAgeSeconds = 180,
});

AutomaticPaperValuationPortfolioCandidate Candidate(
    DateTimeOffset? lastSnapshot,
    params AutomaticPaperValuationPositionCandidate[] positions) => new(
        1,
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PAPER-ALPHA",
        "INR",
        lastSnapshot,
        positions);

AutomaticPaperValuationPositionCandidate Position(
    long id,
    string direction,
    decimal quantity,
    decimal average,
    decimal realized,
    decimal fees,
    decimal taxes,
    string? providerKey) => new(
        id,
        Guid.Parse($"{id:D8}-0000-0000-0000-000000000001"),
        1000 + id,
        $"NSE|POSITION{id}",
        "INTRADAY",
        direction,
        quantity,
        average,
        quantity * average,
        realized,
        fees,
        taxes,
        1,
        providerKey);

StoredCandleV1 Candle(
    long id,
    string instrumentKey,
    DateTimeOffset closeAt,
    decimal closePrice) => new(
        id,
        Guid.Parse($"{id:D8}-0000-0000-0000-000000000002"),
        instrumentKey,
        "1m",
        closeAt.AddMinutes(-1),
        closeAt,
        closePrice,
        closePrice,
        closePrice,
        closePrice,
        1000m,
        MarketDataQualityStatusV1.Valid,
        true,
        closeAt.AddSeconds(1));

AutomaticPaperValuationWorkItem WorkItem(int attempt)
{
    var candidate = Candidate(
        null,
        Position(11, "LONG", 10m, 100m, 0m, 0m, 0m, "NSE_EQ|LONG"));
    var decision = Policy().Evaluate(
        candidate,
        new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>
        {
            [11] = [Candle(101, "NSE_EQ|LONG", commonClose, 110m)],
        },
        evaluatedAt);
    var requestUid = AutomaticPaperValuationIdentity.RequestUid(candidate, commonClose, "v1");
    var payload = new AutomaticPaperValuationPayload(
        requestUid,
        AutomaticPaperValuationIdentity.SnapshotUid(requestUid),
        "v1",
        candidate,
        decision,
        evaluatedAt);
    return new AutomaticPaperValuationWorkItem(
        10,
        requestUid,
        payload.SnapshotUid,
        candidate.PortfolioId,
        candidate.PortfolioCode,
        commonClose,
        payload,
        attempt);
}

AutomaticPaperValuationProcessor Processor(
    RecordingQueue queue,
    IPaperPortfolioValuationLedgerStore ledger,
    AutomaticPaperValuationWorkerState state,
    int maximumAttempts = 5) => new(
        new AutomaticPaperValuationOptions { MaximumAttempts = maximumAttempts },
        queue,
        ledger,
        state,
        NullLogger<AutomaticPaperValuationProcessor>.Instance);

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

static void Contains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"Expected reason '{expected}'.");
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

sealed class RecordingQueue : IAutomaticPaperValuationWorkQueue
{
    public string? LastStatus { get; private set; }

    public Task<AutomaticPaperValuationEnqueueResult> EnqueueAsync(
        AutomaticPaperValuationPayload payload,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AutomaticPaperValuationEnqueueResult(
            "ENQUEUED",
            payload.RequestUid,
            Array.Empty<string>()));

    public Task<IReadOnlyCollection<AutomaticPaperValuationWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<AutomaticPaperValuationWorkItem>>([]);

    public Task CompleteAsync(
        long workItemId,
        string resultStatus,
        Guid snapshotUid,
        CancellationToken cancellationToken)
    {
        LastStatus = resultStatus;
        return Task.CompletedTask;
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperValuationStatus.RetryPending;
        return Task.CompletedTask;
    }

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperValuationStatus.Rejected;
        return Task.CompletedTask;
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken)
    {
        LastStatus = AutomaticPaperValuationStatus.Failed;
        return Task.CompletedTask;
    }
}

sealed class FixedLedger(string status) : IPaperPortfolioValuationLedgerStore
{
    public Task<PortfolioValuationPersistenceResultV1> PersistAsync(
        AutomaticPaperValuationWorkItem workItem,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PortfolioValuationPersistenceResultV1(
            workItem.RequestUid,
            status,
            Array.Empty<string>(),
            null,
            DateTimeOffset.UtcNow));

    public Task<PortfolioPnlSnapshotV1?> GetLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken) =>
        Task.FromResult<PortfolioPnlSnapshotV1?>(null);
}

sealed class ThrowingLedger(Exception exception) : IPaperPortfolioValuationLedgerStore
{
    public Task<PortfolioValuationPersistenceResultV1> PersistAsync(
        AutomaticPaperValuationWorkItem workItem,
        CancellationToken cancellationToken) =>
        Task.FromException<PortfolioValuationPersistenceResultV1>(exception);

    public Task<PortfolioPnlSnapshotV1?> GetLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
