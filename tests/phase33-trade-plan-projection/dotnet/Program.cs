using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

var failures = new List<string>();
var projector = new DeterministicAutomaticTradePlanProjector();
var builder = new DeterministicTradePlanBuilder(Options.Create(new DeterministicTradePlanOptions()));

Run("approved Risk projects replay-stable Trade Plan command", () =>
{
    var intake = CreateIntake(DateTimeOffset.UtcNow);
    var first = projector.Project(intake);
    var second = projector.Project(intake);

    Equal(AutomaticTradePlanContractV1.Eligible, first.Outcome);
    var firstCommand = first.Command ?? throw new InvalidOperationException("Eligible projection requires a command.");
    var secondCommand = second.Command ?? throw new InvalidOperationException("Replay requires a command.");
    Equal(firstCommand.CommandUid, secondCommand.CommandUid);
    Equal(firstCommand.RequestUid, secondCommand.RequestUid);
    Equal(intake.RiskDecision.RiskDecisionUid, firstCommand.RiskDecisionUid);
    Equal(intake.Signal.SignalUid, firstCommand.SignalUid);
});

Run("projected command builds bounded non-executable plan", () =>
{
    var intake = CreateIntake(DateTimeOffset.UtcNow);
    var projection = projector.Project(intake);
    var command = projection.Command ?? throw new InvalidOperationException("Expected projected command.");
    var result = builder.Build(command.Request);

    Equal(TradePlanContractV1.Ready, result.Status);
    var plan = result.TradePlan ?? throw new InvalidOperationException("READY result requires a plan.");
    Equal("BUY", plan.Side);
    Equal(110m, plan.Targets.OrderBy(target => target.Sequence).First().Price);
    False(plan.ExecutionAuthorized);
    True(plan.ValidUntilUtc <= intake.RiskDecision.Budget!.ExpiresAtUtc);
});

Run("non-approved Risk decision fails closed", () =>
{
    var intake = CreateIntake(DateTimeOffset.UtcNow);
    intake = intake with
    {
        RiskDecision = intake.RiskDecision with
        {
            Decision = RiskDecisionContractV1.Rejected,
            Reasons = new[] { "DAILY_LOSS_LIMIT" },
            Budget = null,
        },
    };

    var result = projector.Project(intake);
    Equal(AutomaticTradePlanContractV1.Rejected, result.Outcome);
    Contains("RISK_DECISION_NOT_APPROVED", result.Reasons);
    Null(result.Command);
});

Run("Risk and Signal lineage mismatch fails closed", () =>
{
    var intake = CreateIntake(DateTimeOffset.UtcNow);
    intake = intake with { Signal = intake.Signal with { SignalUid = Guid.NewGuid() } };

    var result = projector.Project(intake);
    Equal(AutomaticTradePlanContractV1.Rejected, result.Outcome);
    Contains("RISK_SIGNAL_LINEAGE_MISMATCH", result.Reasons);
});

Run("expired Signal and Risk budget fail closed", () =>
{
    var now = DateTimeOffset.UtcNow;
    var intake = CreateIntake(now);
    intake = intake with
    {
        Signal = intake.Signal with
        {
            EntryClosesAtUtc = now.AddSeconds(-1),
            ValidUntilUtc = now.AddSeconds(-1),
        },
        RiskDecision = intake.RiskDecision with
        {
            Budget = intake.RiskDecision.Budget! with { ExpiresAtUtc = now.AddSeconds(-1) },
        },
    };

    var result = projector.Project(intake);
    Equal(AutomaticTradePlanContractV1.Rejected, result.Outcome);
    Contains("SIGNAL_EXPIRED", result.Reasons);
    Contains("RISK_BUDGET_EXPIRED_OR_MISSING", result.Reasons);
});

Run("invalid target fractions fail closed", () =>
{
    var intake = CreateIntake(DateTimeOffset.UtcNow);
    intake = intake with
    {
        Execution = intake.Execution with
        {
            Targets = new[]
            {
                new TradePlanTargetPolicyV1(1, 2m, 0.4m),
                new TradePlanTargetPolicyV1(2, 3m, 0.4m),
            },
        },
    };

    var result = projector.Project(intake);
    Equal(AutomaticTradePlanContractV1.Rejected, result.Outcome);
    Contains("TARGET_FRACTIONS_INVALID", result.Reasons);
});

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("All Phase 3.3 automatic Trade Plan projection tests passed.");
return 0;

void Run(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
}

static AutomaticTradePlanIntakeV1 CreateIntake(DateTimeOffset now)
{
    var signalUid = Guid.NewGuid();
    var thesisUid = Guid.NewGuid();
    var decision = new RiskDecisionV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        signalUid,
        thesisUid,
        "NSE_EQ|INE002A01018",
        "PAPER",
        EvidenceDirectionV1.Long,
        RiskDecisionContractV1.Approved,
        Array.Empty<string>(),
        Array.Empty<RiskCheckV1>(),
        new RiskBudgetV1(10000m, 200000m, 100m, now.AddMinutes(2)),
        "risk-policy-v1.0.0",
        "deterministic-risk-v1.0.0",
        now.AddSeconds(-2));
    var signal = new SignalGeneratedV1(
        signalUid,
        decision.InstrumentKey,
        "THESIS_FUSION",
        "deterministic-thesis-fusion-v1.0.0",
        "LONG",
        "5m",
        new[] { "5m", "15m" },
        0.8m,
        0.75m,
        now.AddSeconds(-10),
        now.AddMinutes(1),
        100m,
        99.5m,
        100.5m,
        95m,
        "Deterministic invalidation.",
        30,
        now.AddSeconds(-10),
        now.AddMinutes(2),
        "fusion-policy-v1.0.0",
        Array.Empty<SignalEvidenceV1>());
    var execution = new TradePlanExecutionContextV1(
        "INTRADAY",
        "MARKET",
        "STOP_MARKET",
        null,
        0.001m,
        "DAY",
        new[]
        {
            new TradePlanTargetPolicyV1(1, 2m, 0.5m),
            new TradePlanTargetPolicyV1(2, 3m, 0.5m),
        },
        new TradeSessionV1(
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddHours(5)),
        new ExitPolicyV1(true, true, true, true, "exit-policy-v1.0.0"),
        "execution-policy-v1.0.0");

    return new AutomaticTradePlanIntakeV1(
        Guid.NewGuid(),
        decision.CorrelationId,
        Guid.NewGuid(),
        decision,
        signal,
        new TradePlanInstrumentContextV1(1m, null, 1m, true),
        execution,
        now);
}

static void Contains(string expected, IReadOnlyCollection<string> actual)
{
    if (!actual.Contains(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"Expected '{expected}'.");
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);
static void Null(object? value)
{
    if (value is not null) throw new InvalidOperationException("Expected null.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
}
