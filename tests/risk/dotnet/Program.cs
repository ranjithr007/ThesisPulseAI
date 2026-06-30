using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

var failures = new List<string>();
var engine = new DeterministicRiskDecisionEngine(Options.Create(new DeterministicRiskOptions()));

Run("healthy paper candidate is approved", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = engine.Evaluate(CreateRequest(now));

    AssertEqual(RiskDecisionContractV1.Approved, result.Decision);
    var budget = result.Budget ?? throw new InvalidOperationException(
        "Approved risk decision must contain a bounded risk budget.");
    AssertEqual(10000m, budget.MaximumRiskAmount);
    AssertEqual(200000m, budget.MaximumCapitalAllocation);
});

Run("kill switch rejects candidate", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        Operations = request.Operations with { KillSwitchActive = true },
    };

    var result = engine.Evaluate(request);

    AssertEqual(RiskDecisionContractV1.Rejected, result.Decision);
    AssertContains("KILL_SWITCH_CLEAR", result.Reasons);
    AssertNull(result.Budget, "Rejected decision must not expose a risk budget.");
});

Run("stale candidate is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        Candidate = request.Candidate with { GeneratedAtUtc = now.AddMinutes(-5) },
    };

    var result = engine.Evaluate(request);

    AssertEqual(RiskDecisionContractV1.Rejected, result.Decision);
    AssertContains("CANDIDATE_FRESHNESS", result.Reasons);
});

Run("daily loss breach is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        Portfolio = request.Portfolio with
        {
            RealizedPnlToday = -25000m,
            UnrealizedPnlToday = 0m,
        },
    };

    var result = engine.Evaluate(request);

    AssertEqual(RiskDecisionContractV1.Rejected, result.Decision);
    AssertContains("DAILY_LOSS_LIMIT", result.Reasons);
});

Run("existing instrument position rejects when pyramiding is disabled", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    var position = new PortfolioPositionV1(
        request.Candidate.InstrumentKey,
        EvidenceDirectionV1.Long,
        100000m,
        now.AddMinutes(-20));
    request = request with
    {
        Portfolio = request.Portfolio with
        {
            OpenPositionCount = 1,
            Positions = new[] { position },
        },
    };

    var result = engine.Evaluate(request);

    AssertEqual(RiskDecisionContractV1.Rejected, result.Decision);
    AssertContains("INSTRUMENT_CONCENTRATION", result.Reasons);
});

Run("live environment remains disabled", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        Portfolio = request.Portfolio with { Environment = "LIVE" },
    };

    var result = engine.Evaluate(request);

    AssertEqual(RiskDecisionContractV1.Rejected, result.Decision);
    AssertContains("ENVIRONMENT_ALLOWED", result.Reasons);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} deterministic risk test(s) failed.");
    return 1;
}

Console.WriteLine("All deterministic risk decision tests passed.");
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

static RiskDecisionRequestV1 CreateRequest(DateTimeOffset now)
{
    const string instrumentKey = "NSE_EQ|INE002A01018";
    var candidate = new CanonicalCandidateSignalV1(
        Guid.NewGuid(),
        ThesisFusionContractV1.CandidateStatus,
        instrumentKey,
        EvidenceDirectionV1.Long,
        "5m",
        80m,
        80m,
        now.AddSeconds(-10),
        "fusion-weights-v1.0.0",
        Guid.NewGuid());

    var portfolio = new PortfolioRiskSnapshotV1(
        "paper-account-1",
        "PAPER",
        1000000m,
        500000m,
        200000m,
        200000m,
        -5000m,
        0m,
        1m,
        0,
        Array.Empty<PortfolioPositionV1>(),
        now.AddSeconds(-5));

    var operations = new OperationalRiskStateV1(
        false,
        false,
        true,
        true,
        true,
        true,
        now.AddSeconds(-2));

    return new RiskDecisionRequestV1(
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        candidate,
        portfolio,
        operations,
        "risk-policy-v1.0.0",
        now);
}

static void AssertContains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }
}

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
