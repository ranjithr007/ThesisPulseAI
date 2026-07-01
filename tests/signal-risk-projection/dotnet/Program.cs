using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

var failures = new List<string>();
var projector = new DeterministicSignalRiskProjector();

Run("eligible signal projects deterministic risk command", () =>
{
    var now = DateTimeOffset.UtcNow;
    var intake = CreateIntake(now);

    var first = projector.Project(intake);
    var second = projector.Project(intake);

    AssertEqual(SignalRiskEvaluationContractV1.Eligible, first.Outcome);
    var firstCommand = first.Command ?? throw new InvalidOperationException("Eligible intake must produce a command.");
    var secondCommand = second.Command ?? throw new InvalidOperationException("Replay must produce a command.");
    AssertEqual(firstCommand.CommandUid, secondCommand.CommandUid);
    AssertEqual(firstCommand.RequestUid, secondCommand.RequestUid);
    AssertEqual(intake.Signal.SignalUid, firstCommand.SignalUid);
    AssertEqual(intake.Lineage.ThesisUid, firstCommand.ThesisUid);
    AssertEqual(80m, firstCommand.Request.Candidate.Strength);
    AssertEqual(75m, firstCommand.Request.Candidate.Confidence);
});

Run("expired signal fails closed", () =>
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
    };

    var result = projector.Project(intake);

    AssertEqual(SignalRiskEvaluationContractV1.Rejected, result.Outcome);
    AssertNull(result.Command, "Expired signal must not create a risk command.");
    AssertContains("SIGNAL_EXPIRED", result.Reasons);
});

Run("signal and lineage mismatch fails closed", () =>
{
    var now = DateTimeOffset.UtcNow;
    var intake = CreateIntake(now);
    intake = intake with
    {
        Lineage = intake.Lineage with { CandidateSignalUid = Guid.NewGuid() },
    };

    var result = projector.Project(intake);

    AssertEqual(SignalRiskEvaluationContractV1.Rejected, result.Outcome);
    AssertNull(result.Command, "Lineage mismatch must not create a risk command.");
    AssertContains("SIGNAL_LINEAGE_MISMATCH", result.Reasons);
});

Run("unsupported neutral direction fails closed", () =>
{
    var now = DateTimeOffset.UtcNow;
    var intake = CreateIntake(now);
    intake = intake with
    {
        Signal = intake.Signal with { Direction = "NEUTRAL" },
    };

    var result = projector.Project(intake);

    AssertEqual(SignalRiskEvaluationContractV1.Rejected, result.Outcome);
    AssertContains("SIGNAL_DIRECTION_UNSUPPORTED", result.Reasons);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} signal-to-risk projection test(s) failed.");
    return 1;
}

Console.WriteLine("All deterministic signal-to-risk projection tests passed.");
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

static SignalRiskEvaluationIntakeV1 CreateIntake(DateTimeOffset now)
{
    var signalUid = Guid.NewGuid();
    var signal = new SignalGeneratedV1(
        signalUid,
        "NSE_EQ|INE002A01018",
        "THESIS_FUSION",
        "deterministic-thesis-fusion-v1.0.0",
        "LONG",
        "5m",
        new[] { "5m", "15m" },
        0.80m,
        0.75m,
        now.AddSeconds(-10),
        now.AddMinutes(1),
        100m,
        99m,
        101m,
        98m,
        "Deterministic invalidation.",
        30,
        now.AddSeconds(-10),
        now.AddMinutes(2),
        "fusion-policy-v1.0.0",
        Array.Empty<SignalEvidenceV1>());
    var lineage = new FusionSignalLineageV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        signalUid,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "deterministic-thesis-fusion-v1.0.0",
        "fusion-policy-v1.0.0",
        "fusion-weights-v1.0.0");
    var portfolio = new PortfolioRiskSnapshotV1(
        "paper-account-1",
        "PAPER",
        1000000m,
        500000m,
        100000m,
        100000m,
        0m,
        0m,
        0m,
        0,
        Array.Empty<PortfolioPositionV1>(),
        now.AddSeconds(-2));
    var operations = new OperationalRiskStateV1(
        false,
        false,
        true,
        true,
        true,
        true,
        now.AddSeconds(-1));

    return new SignalRiskEvaluationIntakeV1(
        Guid.NewGuid(),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid(),
        signal,
        lineage,
        portfolio,
        operations,
        "risk-policy-v1.0.0",
        now);
}

static void AssertContains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
}

static void AssertNull(object? value, string message)
{
    if (value is not null)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
}
