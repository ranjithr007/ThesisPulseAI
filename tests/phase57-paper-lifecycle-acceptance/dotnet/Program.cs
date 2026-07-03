using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;

var failures = new List<string>();
var evaluator = new PaperTradeLifecycleAcceptanceEvaluator();
var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

Run("complete authoritative lifecycle passes", () =>
{
    var detail = CompleteDetail(now);
    var report = Evaluate(detail, CompleteLineage(detail, now), SafeStates(detail, now));
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Passed,
        $"Expected PASS, received {report.Outcome}.");
    Assert(report.Checks.All(check => check.Outcome == PaperTradeLifecycleAcceptanceContractV1.Passed),
        "Every complete-lifecycle check must pass.");
});

Run("missing P&L evidence remains incomplete", () =>
{
    var complete = CompleteDetail(now);
    var lineage = CompleteLineage(complete, now)
        .Where(item => item.Stage != "PNL")
        .ToArray();
    var incomplete = complete with
    {
        Summary = complete.Summary with
        {
            LifecycleStage = "POSITION_POSTED",
            LifecycleStatus = PaperTradeLifecycleContractV1.InProgress,
            IsComplete = false,
            PnlSnapshotUid = null,
            NetPnlAmount = null,
        },
    };

    var report = Evaluate(incomplete, lineage, SafeStates(incomplete, now));
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Incomplete,
        $"Expected INCOMPLETE, received {report.Outcome}.");
    Assert(Check(report, "MANDATORY_STAGE_COVERAGE").Outcome ==
           PaperTradeLifecycleAcceptanceContractV1.Incomplete,
        "Missing P&L must be reported as incomplete stage coverage.");
});

Run("self-referential causation fails", () =>
{
    var detail = CompleteDetail(now);
    var lineage = CompleteLineage(detail, now).ToList();
    var index = lineage.FindIndex(item => item.Stage == "THESIS");
    lineage[index] = lineage[index] with { CausationUid = lineage[index].EntityUid };

    var report = Evaluate(detail, lineage, SafeStates(detail, now));
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Failed,
        $"Expected FAIL, received {report.Outcome}.");
    Assert(Check(report, "CAUSATION_EVIDENCE").Outcome ==
           PaperTradeLifecycleAcceptanceContractV1.Failed,
        "Self-referential causation must fail explicitly.");
});

Run("restrictive mode cannot allow new exposure", () =>
{
    var detail = CompleteDetail(now);
    var states = new[]
    {
        State("STRATEGY", detail.Summary.StrategyCode, "CLOSE_ONLY", true, true, now),
    };
    var report = Evaluate(detail, CompleteLineage(detail, now), states);
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Failed,
        $"Expected FAIL, received {report.Outcome}.");
    Assert(Check(report, "RESTRICTIVE_MODE_EXPOSURE_SAFETY").Outcome ==
           PaperTradeLifecycleAcceptanceContractV1.Failed,
        "CLOSE_ONLY with new exposure must fail.");
});

Run("risk-reducing exits must remain available", () =>
{
    var detail = CompleteDetail(now);
    var states = new[]
    {
        State("PLATFORM", "THESISPULSE", "PAUSED", false, false, now),
    };
    var report = Evaluate(detail, CompleteLineage(detail, now), states);
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Failed,
        $"Expected FAIL, received {report.Outcome}.");
    Assert(Check(report, "RISK_REDUCING_EXIT_SAFETY").Outcome ==
           PaperTradeLifecycleAcceptanceContractV1.Failed,
        "Blocked risk-reducing exits must fail.");
});

Run("stale lifecycle fails", () =>
{
    var detail = CompleteDetail(now);
    var stale = detail with { Summary = detail.Summary with { IsStale = true } };
    var report = Evaluate(stale, CompleteLineage(stale, now), SafeStates(stale, now));
    Assert(report.Outcome == PaperTradeLifecycleAcceptanceContractV1.Failed,
        $"Expected FAIL, received {report.Outcome}.");
    Assert(Check(report, "FRESHNESS").Outcome ==
           PaperTradeLifecycleAcceptanceContractV1.Failed,
        "Stale evidence must fail freshness.");
});

await RunAsync("non-SQL acceptance store fails closed", async () =>
{
    IPaperTradeLifecycleAcceptanceStore store = new UnavailablePaperTradeLifecycleAcceptanceStore();
    Assert(!store.IsAvailable, "Unavailable store must not report readiness.");
    Assert(!string.IsNullOrWhiteSpace(store.UnavailableReason),
        "Unavailable store must expose a reason.");
    var report = await store.ReadAsync(Guid.NewGuid(), "PAPER-DEFAULT", CancellationToken.None);
    Assert(report is null, "Unavailable store must not fabricate an acceptance report.");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Phase 5.7 acceptance failed with {failures.Count} error(s):");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("Phase 5.7 PAPER lifecycle acceptance tests passed.");
return 0;

PaperTradeLifecycleAcceptanceReportV1 Evaluate(
    PaperTradeLifecycleDetailV1 detail,
    IReadOnlyCollection<PaperTradeLifecycleLineageEvidenceV1> lineage,
    IReadOnlyCollection<PaperTradeLifecycleOperationalStateV1> states) =>
    evaluator.Evaluate(
        PaperTradeLifecycleContractV1.PaperEnvironment,
        detail,
        lineage,
        states,
        now);

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

static PaperTradeLifecycleDetailV1 CompleteDetail(DateTimeOffset now)
{
    var summary = new PaperTradeLifecycleSummaryV1(
        Guid.NewGuid(), "PAPER-DEFAULT", "NSE_EQ|RELIANCE", "INTRADAY-MOMENTUM", "LONG",
        "PNL_VALUED", PaperTradeLifecycleContractV1.Complete, true, false,
        now.AddSeconds(-1), now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
        10m, 10m, 2500m, 10m, 125m, Array.Empty<string>());
    return new PaperTradeLifecycleDetailV1(
        PaperTradeLifecycleContractV1.ContractVersion,
        summary,
        Array.Empty<PaperTradeLifecycleStageV1>());
}

static IReadOnlyCollection<PaperTradeLifecycleLineageEvidenceV1> CompleteLineage(
    PaperTradeLifecycleDetailV1 detail,
    DateTimeOffset now)
{
    var summary = detail.Summary;
    return
    [
        Evidence("SIGNAL", summary.SignalUid, summary.CorrelationUid, null, now.AddMinutes(-9), "intelligence.signals"),
        Evidence("THESIS", summary.ThesisUid!.Value, summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-8), "thesis.theses"),
        Evidence("RISK", summary.RiskDecisionUid!.Value, summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-7), "risk.risk_decisions"),
        Evidence("TRADE_PLAN", summary.TradePlanUid, summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-6), "risk.trade_plans"),
        Evidence("EXECUTION_COMMAND", summary.ExecutionCommandUid!.Value, summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-5), "execution.execution_commands"),
        Evidence("ORDER_EVENT", Guid.NewGuid(), summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-4), "execution.order_events"),
        Evidence("FILL", Guid.NewGuid(), summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-3), "execution.fills"),
        Evidence("POSITION_EVENT", Guid.NewGuid(), summary.CorrelationUid, Guid.NewGuid(), now.AddMinutes(-2), "portfolio.position_events"),
        Evidence("PNL", summary.PnlSnapshotUid!.Value, summary.CorrelationUid, null, now.AddMinutes(-1), "portfolio.pnl_snapshots"),
    ];
}

static PaperTradeLifecycleLineageEvidenceV1 Evidence(
    string stage,
    Guid entityUid,
    Guid correlationUid,
    Guid? causationUid,
    DateTimeOffset occurredAtUtc,
    string sourceTable) =>
    new(stage, entityUid, correlationUid, causationUid, occurredAtUtc, sourceTable);

static IReadOnlyCollection<PaperTradeLifecycleOperationalStateV1> SafeStates(
    PaperTradeLifecycleDetailV1 detail,
    DateTimeOffset now) =>
[
    State("STRATEGY", detail.Summary.StrategyCode, "NORMAL", true, true, now),
];

static PaperTradeLifecycleOperationalStateV1 State(
    string scopeType,
    string scopeId,
    string mode,
    bool allowsNewExposure,
    bool allowsRiskReducingExits,
    DateTimeOffset now) =>
    new(scopeType, scopeId, mode, mode == "NORMAL" ? null : Guid.NewGuid(), allowsNewExposure,
        allowsRiskReducingExits, mode != "NORMAL", now, "test-v1");

static PaperTradeLifecycleAcceptanceCheckV1 Check(
    PaperTradeLifecycleAcceptanceReportV1 report,
    string code) => report.Checks.Single(item => item.Code == code);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
