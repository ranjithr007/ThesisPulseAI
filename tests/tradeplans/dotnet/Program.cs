using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

var failures = new List<string>();
var builder = new DeterministicTradePlanBuilder(
    Options.Create(new DeterministicTradePlanOptions()));

Run("approved long decision creates bounded non-executable plan", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = builder.Build(CreateLongRequest(now));

    AssertEqual(TradePlanContractV1.Ready, result.Status);
    var plan = result.TradePlan ?? throw new InvalidOperationException("READY result must contain a trade plan.");
    AssertEqual("BUY", plan.Side);
    AssertEqual(1818m, plan.ApprovedQuantity);
    AssertEqual(9999m, plan.RiskAmountAtStop);
    AssertEqual(182709m, plan.CapitalAtReference);
    AssertFalse(plan.ExecutionAuthorized, "A trade plan must not authorize execution.");
    AssertTrue(plan.ValidUntilUtc <= now.AddSeconds(60), "Plan validity must not outlive the risk budget.");
});

Run("approved short decision creates sell plan", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now);
    request = request with
    {
        RiskDecision = request.RiskDecision with { Direction = EvidenceDirectionV1.Short },
        StopLossPrice = 105.5m,
        Targets = new[]
        {
            new TradeTargetProposalV1(1, 90m, 0.5m),
            new TradeTargetProposalV1(2, 85m, 0.5m),
        },
    };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Ready, result.Status);
    var plan = result.TradePlan ?? throw new InvalidOperationException("READY result must contain a trade plan.");
    AssertEqual("SELL", plan.Side);
    AssertEqual(1666m, plan.ApprovedQuantity);
    AssertFalse(plan.ExecutionAuthorized, "A SELL plan must remain non-executable.");
});

Run("rejected risk decision cannot create a plan", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now);
    request = request with
    {
        RiskDecision = request.RiskDecision with
        {
            Decision = RiskDecisionContractV1.Rejected,
            Reasons = new[] { "DAILY_LOSS_LIMIT" },
            Budget = null,
        },
    };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Rejected, result.Status);
    AssertContains("RISK_DECISION_APPROVED", result.Reasons);
    AssertNull(result.TradePlan, "Rejected risk decision must not produce a trade plan.");
});

Run("expired risk budget cannot create a plan", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now);
    var budget = request.RiskDecision.Budget ?? throw new InvalidOperationException("Test requires risk budget.");
    request = request with
    {
        RiskDecision = request.RiskDecision with
        {
            Budget = budget with { ExpiresAtUtc = now.AddSeconds(-1) },
        },
    };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Rejected, result.Status);
    AssertContains("RISK_DECISION_CURRENT", result.Reasons);
});

Run("long stop above entry band is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now) with { StopLossPrice = 101m };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Rejected, result.Status);
    AssertContains("STOP_DIRECTION", result.Reasons);
});

Run("target fractions must total one", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now) with
    {
        Targets = new[]
        {
            new TradeTargetProposalV1(1, 110m, 0.4m),
            new TradeTargetProposalV1(2, 115m, 0.5m),
        },
    };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Rejected, result.Status);
    AssertContains("TARGET_FRACTIONS", result.Reasons);
});

Run("budget that cannot fund one lot is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateLongRequest(now);
    var budget = request.RiskDecision.Budget ?? throw new InvalidOperationException("Test requires risk budget.");
    request = request with
    {
        RiskDecision = request.RiskDecision with
        {
            Budget = budget with
            {
                MaximumRiskAmount = 1m,
                MaximumCapitalAllocation = 50m,
            },
        },
    };

    var result = builder.Build(request);

    AssertEqual(TradePlanContractV1.Rejected, result.Status);
    AssertContains("APPROVED_QUANTITY_POSITIVE", result.Reasons);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} deterministic trade-plan test(s) failed.");
    return 1;
}

Console.WriteLine("All deterministic trade-plan builder tests passed.");
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

static TradePlanBuildRequestV1 CreateLongRequest(DateTimeOffset now)
{
    var decision = new RiskDecisionV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "NSE_EQ|INE002A01018",
        "PAPER",
        EvidenceDirectionV1.Long,
        RiskDecisionContractV1.Approved,
        Array.Empty<string>(),
        Array.Empty<RiskCheckV1>(),
        new RiskBudgetV1(10000m, 200000m, 100m, now.AddSeconds(60)),
        "risk-policy-v1.0.0",
        "deterministic-risk-v1.0.0",
        now.AddSeconds(-2));

    return new TradePlanBuildRequestV1(
        Guid.NewGuid(),
        decision.CorrelationId,
        decision,
        "INTRADAY",
        new TradeEntryProposalV1(
            "MARKET",
            100m,
            null,
            null,
            99.5m,
            100.5m),
        95m,
        "STOP_MARKET",
        null,
        new[]
        {
            new TradeTargetProposalV1(1, 110m, 0.5m),
            new TradeTargetProposalV1(2, 115m, 0.5m),
        },
        1m,
        null,
        1m,
        true,
        0.001m,
        "DAY",
        new TradeSessionV1(
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddHours(5)),
        new ExitPolicyV1(
            true,
            true,
            true,
            true,
            "exit-policy-v1.0.0"),
        "execution-policy-v1.0.0",
        now);
}

static void AssertContains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message) => AssertTrue(!value, message);

static void AssertNull(object? value, string message)
{
    if (value is not null)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }
}
