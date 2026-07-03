using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;

var failures = new List<string>();

Run("default options are valid", () =>
{
    var options = new PaperTradeLifecycleReadOptions();
    options.Validate();
    Assert(options.DefaultLimit == 50, "Unexpected default limit.");
    Assert(options.MaximumLimit == 200, "Unexpected maximum limit.");
    Assert(options.MaximumAgeMinutes == 15, "Unexpected maximum age.");
});

Run("invalid options fail closed", () =>
{
    AssertThrows(() => new PaperTradeLifecycleReadOptions
    {
        DefaultLimit = 0,
    }.Validate());
    AssertThrows(() => new PaperTradeLifecycleReadOptions
    {
        DefaultLimit = 50,
        MaximumLimit = 20,
    }.Validate());
    AssertThrows(() => new PaperTradeLifecycleReadOptions
    {
        MaximumAgeMinutes = 0,
    }.Validate());
});

await RunAsync("non-SQL store reports unavailable", async () =>
{
    IPaperTradeLifecycleReadStore store = new UnavailablePaperTradeLifecycleReadStore();
    Assert(!store.IsAvailable, "Unavailable store must not report readiness.");
    Assert(!string.IsNullOrWhiteSpace(store.UnavailableReason),
        "Unavailable store must expose a reason.");
    var recent = await store.ReadRecentAsync("PAPER-DEFAULT", 10, CancellationToken.None);
    Assert(recent.Count == 0, "Unavailable store must not fabricate lifecycle rows.");
    var detail = await store.ReadAsync(Guid.NewGuid(), "PAPER-DEFAULT", CancellationToken.None);
    Assert(detail is null, "Unavailable store must not fabricate lifecycle detail.");
});

Run("contract preserves authoritative lineage", () =>
{
    var now = DateTimeOffset.UtcNow;
    var correlationUid = Guid.NewGuid();
    var signalUid = Guid.NewGuid();
    var tradePlanUid = Guid.NewGuid();
    var summary = new PaperTradeLifecycleSummaryV1(
        correlationUid,
        "PAPER-DEFAULT",
        "NSE_EQ|RELIANCE",
        "INTRADAY-MOMENTUM",
        "LONG",
        "TRADE_PLAN_READY",
        PaperTradeLifecycleContractV1.InProgress,
        false,
        false,
        now,
        now,
        signalUid,
        null,
        null,
        tradePlanUid,
        null,
        null,
        0,
        null,
        null,
        10m,
        null,
        null,
        null,
        null,
        new[] { "THESIS_LINEAGE_NOT_AVAILABLE" });

    Assert(summary.CorrelationUid == correlationUid, "Correlation UID changed.");
    Assert(summary.SignalUid == signalUid, "Signal UID changed.");
    Assert(summary.TradePlanUid == tradePlanUid, "Trade Plan UID changed.");
    Assert(summary.Warnings.Single() == "THESIS_LINEAGE_NOT_AVAILABLE",
        "Warnings must remain explicit.");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Phase 5.5 acceptance failed with {failures.Count} error(s):");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("Phase 5.5 PAPER lifecycle acceptance passed.");
return 0;

void Run(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

async Task RunAsync(string name, Func<Task> action)
{
    try
    {
        await action();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertThrows(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
}
