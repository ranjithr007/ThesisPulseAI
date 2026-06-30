using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Portfolio;

var failures = new List<string>();

Run("same-side fills increase weighted-average position", () =>
{
    var opened = DeterministicPositionAccounting.ApplyFill(
        FlatState(),
        Array.Empty<PositionLotState>(),
        Fill("BUY", 10m, 100m));
    var increased = DeterministicPositionAccounting.ApplyFill(
        opened.After,
        opened.NewLots,
        Fill("BUY", 10m, 110m));

    AssertEqual("INCREASED", increased.EventType);
    AssertEqual(EvidenceDirectionV1.Long, increased.After.Direction);
    AssertEqual(20m, increased.After.Quantity);
    AssertEqual(105m, increased.After.AverageOpenPrice ?? 0m);
    AssertEqual(2100m, increased.After.CostBasisAmount);
});

Run("opposing fill closes FIFO lots and recognizes net PnL", () =>
{
    var now = DateTimeOffset.UtcNow;
    var opened = DeterministicPositionAccounting.ApplyFill(
        FlatState(),
        Array.Empty<PositionLotState>(),
        new PositionFillInput(Guid.NewGuid(), "BUY", 10m, 100m, 1m, 1m, now));
    var closed = DeterministicPositionAccounting.ApplyFill(
        opened.After,
        opened.NewLots,
        new PositionFillInput(Guid.NewGuid(), "SELL", 4m, 110m, 1m, 1m, now.AddSeconds(1)));

    AssertEqual("REDUCED", closed.EventType);
    AssertEqual(6m, closed.After.Quantity);
    AssertEqual(100m, closed.After.AverageOpenPrice ?? 0m);
    AssertEqual(40m, closed.GrossRealizedPnlDelta);
    AssertEqual(37.2m, closed.NetRealizedPnlDelta);
    AssertEqual(37.2m, closed.After.RealizedPnlAmount);
    AssertEqual(1, closed.Closures.Count);
});

Run("oversized opposing fill reverses position", () =>
{
    var opened = DeterministicPositionAccounting.ApplyFill(
        FlatState(),
        Array.Empty<PositionLotState>(),
        Fill("BUY", 5m, 100m));
    var reversed = DeterministicPositionAccounting.ApplyFill(
        opened.After,
        opened.NewLots,
        Fill("SELL", 8m, 90m));

    AssertEqual("REVERSED", reversed.EventType);
    AssertEqual(EvidenceDirectionV1.Short, reversed.After.Direction);
    AssertEqual(3m, reversed.After.Quantity);
    AssertEqual(90m, reversed.After.AverageOpenPrice ?? 0m);
    AssertEqual(-50m, reversed.NetRealizedPnlDelta);
    AssertEqual(1, reversed.NewLots.Count);
});

Run("fill projection is idempotent and updates cash", () =>
{
    var store = new InMemoryPortfolioLedgerStore();
    var fill = new PortfolioFillRecord(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "PAPER",
        "NSE|RELIANCE",
        "INTRADAY",
        "BUY",
        10m,
        100m,
        1m,
        1m,
        "INR",
        DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString("D"));
    store.RegisterFill(fill);
    var request = new PortfolioFillProjectionRequestV1(
        Guid.NewGuid(),
        fill.FillUid,
        "PAPER-ALPHA",
        fill.CorrelationId,
        fill.FilledAtUtc);

    var first = store.ProjectFillAsync(request).GetAwaiter().GetResult();
    var second = store.ProjectFillAsync(request with { RequestUid = Guid.NewGuid() })
        .GetAwaiter()
        .GetResult();

    AssertEqual(PortfolioLedgerContractV1.Projected, first.Status);
    AssertEqual(PortfolioLedgerContractV1.Duplicate, second.Status);
    AssertEqual(10m, first.Position?.Quantity ?? 0m);
    AssertEqual(-1002m, first.Portfolio?.CashBalances.Single().SettledAmount ?? 0m);
    AssertEqual(first.Position?.Version ?? 0, second.Position?.Version ?? -1);
});

Run("portfolio snapshot aggregates gross net and realized PnL", () =>
{
    var store = new InMemoryPortfolioLedgerStore();
    var correlation = Guid.NewGuid().ToString("D");
    var buy = new PortfolioFillRecord(
        Guid.NewGuid(), Guid.NewGuid(), "PAPER", "NSE|INFY", "INTRADAY",
        "BUY", 10m, 100m, 0m, 0m, "INR", DateTimeOffset.UtcNow, correlation);
    var sell = new PortfolioFillRecord(
        Guid.NewGuid(), Guid.NewGuid(), "PAPER", "NSE|INFY", "INTRADAY",
        "SELL", 4m, 110m, 0m, 0m, "INR", buy.FilledAtUtc.AddSeconds(1), correlation);
    store.RegisterFill(buy);
    store.RegisterFill(sell);

    store.ProjectFillAsync(Request(buy, "PAPER-ALPHA")).GetAwaiter().GetResult();
    var result = store.ProjectFillAsync(Request(sell, "PAPER-ALPHA"))
        .GetAwaiter()
        .GetResult();
    var snapshot = result.Portfolio ?? throw new InvalidOperationException("Snapshot required.");

    AssertEqual(1, snapshot.OpenPositionCount);
    AssertEqual(660m, snapshot.GrossExposureAmount);
    AssertEqual(660m, snapshot.NetExposureAmount);
    AssertEqual(40m, snapshot.RealizedPnlAmount);
    AssertEqual(60m, snapshot.UnrealizedPnlAmount);
    AssertEqual(-560m, snapshot.CashBalances.Single().SettledAmount);
});

Run("reconciliation detects quantity mismatch without auto-correction", () =>
{
    var store = new InMemoryPortfolioLedgerStore();
    var fill = new PortfolioFillRecord(
        Guid.NewGuid(), Guid.NewGuid(), "PAPER", "NSE|TCS", "INTRADAY",
        "BUY", 5m, 100m, 0m, 0m, "INR", DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString("D"));
    store.RegisterFill(fill);
    store.ProjectFillAsync(Request(fill, "PAPER-ALPHA")).GetAwaiter().GetResult();
    store.OverridePositionQuantity("PAPER-ALPHA", fill.InstrumentKey, 4m);

    var result = store.ReconcileAsync(new LedgerReconciliationRequestV1(
        Guid.NewGuid(),
        "PAPER-ALPHA",
        fill.CorrelationId,
        "PERIODIC",
        fill.FilledAtUtc.AddSeconds(1)))
        .GetAwaiter()
        .GetResult();
    var snapshot = store.GetSnapshotAsync("PAPER-ALPHA", fill.FilledAtUtc.AddSeconds(1))
        .GetAwaiter()
        .GetResult();

    AssertEqual(PortfolioLedgerContractV1.Discrepant, result.Status);
    AssertTrue(result.BlocksNewExposure, "Material mismatch must block new exposure.");
    AssertTrue(result.AllowsRiskReducingExits, "Risk-reducing exits must remain available.");
    AssertEqual("POSITION_MISMATCH", result.Discrepancies.Single().Type);
    AssertEqual(4m, snapshot?.Positions.Single().Quantity ?? 0m);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} portfolio ledger test(s) failed.");
    return 1;
}

Console.WriteLine("All deterministic portfolio ledger tests passed.");
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
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static PositionAccountingState FlatState() =>
    new(EvidenceDirectionV1.Neutral, 0m, null, 0m, 0m, 0m, 0m, 0);

static PositionFillInput Fill(string side, decimal quantity, decimal price) =>
    new(Guid.NewGuid(), side, quantity, price, 0m, 0m, DateTimeOffset.UtcNow);

static PortfolioFillProjectionRequestV1 Request(
    PortfolioFillRecord fill,
    string portfolioCode) =>
    new(Guid.NewGuid(), fill.FillUid, portfolioCode, fill.CorrelationId, fill.FilledAtUtc);

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected: {expected}; actual: {actual}.");
    }
}
