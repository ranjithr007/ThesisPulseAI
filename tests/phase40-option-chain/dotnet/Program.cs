using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Signal.Service;

var tests = new (string Name, Action Run)[]
{
    ("PCR, max pain and OI walls are deterministic", TestCoreMetrics),
    ("OI activity classification uses price and OI changes", TestActivityClassification),
    ("IV skew and term structure are deterministic", TestIvStructure),
    ("Replay preserves evidence identity", TestReplayIdentity),
    ("Invalid chains fail closed", TestInvalidChain),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void TestCoreMetrics()
{
    var evidence = OptionChainIntelligenceEvaluator.Evaluate(CreateValidInput());

    Equal(1.0m, evidence.PutCallRatioByOpenInterest, "PCR by OI");
    Equal(1.0m, evidence.PutCallRatioByVolume, "PCR by volume");
    Equal(100m, evidence.MaxPainStrike, "max pain");
    Equal(4, evidence.OpenInterestWalls.Count, "wall count");
    True(evidence.OpenInterestWalls.Any(wall => wall.Side == "CALL" && wall.StrikePrice == 110m), "nearest-expiry call wall");
    True(evidence.OpenInterestWalls.Any(wall => wall.Side == "PUT" && wall.StrikePrice == 90m), "nearest-expiry put wall");
}

static void TestActivityClassification()
{
    var evidence = OptionChainIntelligenceEvaluator.Evaluate(CreateValidInput());
    var nearest = new DateOnly(2026, 7, 9);

    Equal(
        OptionChainContractConstantsV1.LongBuildup,
        Activity(evidence, nearest, 90m, "CALL").Classification,
        "long buildup");
    Equal(
        OptionChainContractConstantsV1.ShortBuildup,
        Activity(evidence, nearest, 100m, "CALL").Classification,
        "short buildup");
    Equal(
        OptionChainContractConstantsV1.ShortCovering,
        Activity(evidence, nearest, 110m, "CALL").Classification,
        "short covering");
    Equal(
        OptionChainContractConstantsV1.LongUnwinding,
        Activity(evidence, nearest, 90m, "PUT").Classification,
        "long unwinding");
}

static void TestIvStructure()
{
    var evidence = OptionChainIntelligenceEvaluator.Evaluate(CreateValidInput());
    var nearest = evidence.ImpliedVolatilityStructure.OrderBy(value => value.ExpiryDate).First();

    Equal(0.02m, nearest.PutCallSkew, "ATM put-call skew");
    Equal(0.10m, nearest.WingSkew, "wing skew");
    Equal(0.04m, nearest.TermPremiumToNextExpiry, "term premium");
}

static void TestReplayIdentity()
{
    var input = CreateValidInput();
    var first = OptionChainIntelligenceEvaluator.Evaluate(input);
    var second = OptionChainIntelligenceEvaluator.Evaluate(input with
    {
        RequestUid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        EvaluatedAtUtc = input.EvaluatedAtUtc.AddMinutes(1),
    });

    Equal(first.EvidenceUid, second.EvidenceUid, "evidence UID");
    Equal(first.MaxPainStrike, second.MaxPainStrike, "max pain replay");
    Equal(first.DirectionalBias, second.DirectionalBias, "bias replay");
}

static void TestInvalidChain()
{
    var input = CreateValidInput() with
    {
        Environment = "LIVE",
        Expiries = new[]
        {
            new OptionChainExpiryInputV1(
                new DateOnly(2026, 7, 9),
                100m,
                new[]
                {
                    Strike(100m, 10m, 10m, 0m, 0m, 0m, 0m, 0.20m, 0.20m, null, null),
                }),
        },
    };

    var evidence = OptionChainIntelligenceEvaluator.Evaluate(input);
    True(evidence.RejectionReasons.Contains("PAPER_ENVIRONMENT_REQUIRED"), "LIVE environment rejection");
    True(evidence.RejectionReasons.Contains("INSUFFICIENT_STRIKES"), "insufficient strike rejection");
    Equal(0m, evidence.Confidence, "rejected confidence");
}

static OptionChainEvaluationInputV1 CreateValidInput()
{
    var sourceAsOf = new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
    return new OptionChainEvaluationInputV1(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "NSE:NIFTY50",
        "PAPER",
        sourceAsOf,
        sourceAsOf.AddSeconds(5),
        OptionChainContractConstantsV1.EngineVersion,
        new[]
        {
            new OptionChainExpiryInputV1(
                new DateOnly(2026, 7, 9),
                100m,
                new[]
                {
                    Strike(90m, 100m, 300m, 20m, -10m, 100m, 100m, 0.30m, 0.34m, 2m, -2m),
                    Strike(100m, 200m, 200m, 30m, 15m, 100m, 100m, 0.20m, 0.22m, -1m, 1m),
                    Strike(110m, 300m, 100m, -20m, 25m, 100m, 100m, 0.24m, 0.26m, 1m, -1m),
                }),
            new OptionChainExpiryInputV1(
                new DateOnly(2026, 7, 16),
                100m,
                new[]
                {
                    Strike(90m, 100m, 300m, 10m, 10m, 100m, 100m, 0.34m, 0.38m, 1m, 1m),
                    Strike(100m, 200m, 200m, 10m, 10m, 100m, 100m, 0.24m, 0.26m, 1m, 1m),
                    Strike(110m, 300m, 100m, 10m, 10m, 100m, 100m, 0.28m, 0.30m, 1m, 1m),
                }),
        });
}

static OptionChainStrikeInputV1 Strike(
    decimal strike,
    decimal callOi,
    decimal putOi,
    decimal callOiChange,
    decimal putOiChange,
    decimal callVolume,
    decimal putVolume,
    decimal? callIv,
    decimal? putIv,
    decimal? callPriceChange,
    decimal? putPriceChange) => new(
        strike,
        callOi,
        putOi,
        callOiChange,
        putOiChange,
        callVolume,
        putVolume,
        callIv,
        putIv,
        1m,
        1m,
        callPriceChange,
        putPriceChange);

static OptionChainStrikeActivityV1 Activity(
    OptionChainIntelligenceEvidenceV1 evidence,
    DateOnly expiry,
    decimal strike,
    string side) => evidence.StrikeActivity.Single(activity =>
        activity.ExpiryDate == expiry &&
        activity.StrikePrice == strike &&
        activity.OptionSide == side);

static void True(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
