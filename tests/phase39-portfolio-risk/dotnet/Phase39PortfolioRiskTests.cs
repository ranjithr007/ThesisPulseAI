using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

public static class Phase39PortfolioRiskTests
{
    public static List<string> Run()
    {
        var failures = new List<string>();
        Run(failures, "normal mode", NormalMode);
        Run(failures, "restricted mode", RestrictedMode);
        Run(failures, "hard daily loss", HardDailyLoss);
        Run(failures, "hard weekly loss", HardWeeklyLoss);
        Run(failures, "hard drawdown", HardDrawdown);
        Run(failures, "deterministic snapshot identity", DeterministicIdentity);
        Run(failures, "restricted budget multiplier", RestrictedBudgetMultiplier);
        Run(failures, "hard mode blocks new exposure", HardModeBlocksNewExposure);
        return failures;
    }

    private static void NormalMode()
    {
        var result = PortfolioRiskEvaluator.Evaluate(Input(dailyPnl: 250m));
        Equal(PortfolioRiskContractV1.Normal, result.OperatingMode);
        Equal(1m, result.EffectiveRiskMultiplier);
        True(result.NewExposureAllowed);
        True(result.RiskReducingExitAllowed);
        Contains(result.Reasons, "RISK_LIMITS_CLEAR");
    }

    private static void RestrictedMode()
    {
        var result = PortfolioRiskEvaluator.Evaluate(Input(dailyPnl: -1_200m));
        Equal(PortfolioRiskContractV1.Restricted, result.OperatingMode);
        Equal(0.5m, result.EffectiveRiskMultiplier);
        True(result.NewExposureAllowed);
        Contains(result.Reasons, "DAILY_SOFT_LOSS_LIMIT_REACHED");
    }

    private static void HardDailyLoss()
    {
        var result = PortfolioRiskEvaluator.Evaluate(Input(dailyPnl: -2_000m));
        Equal(PortfolioRiskContractV1.CloseOnly, result.OperatingMode);
        Equal(0m, result.EffectiveRiskMultiplier);
        False(result.NewExposureAllowed);
        True(result.RiskReducingExitAllowed);
        Contains(result.Reasons, "DAILY_LOSS_LIMIT_BREACHED");
    }

    private static void HardWeeklyLoss()
    {
        var result = PortfolioRiskEvaluator.Evaluate(Input(dailyPnl: -200m, weeklyPnl: -3_000m));
        Equal(PortfolioRiskContractV1.CloseOnly, result.OperatingMode);
        Contains(result.Reasons, "WEEKLY_LOSS_LIMIT_BREACHED");
    }

    private static void HardDrawdown()
    {
        var result = PortfolioRiskEvaluator.Evaluate(Input(
            dailyPnl: -100m,
            strategyDrawdown: 0.11m,
            portfolioDrawdown: 0.13m));
        Equal(PortfolioRiskContractV1.CloseOnly, result.OperatingMode);
        Contains(result.Reasons, "STRATEGY_DRAWDOWN_LIMIT_BREACHED");
        Contains(result.Reasons, "PORTFOLIO_DRAWDOWN_LIMIT_BREACHED");
    }

    private static void DeterministicIdentity()
    {
        var input = Input(dailyPnl: -500m);
        var first = PortfolioRiskEvaluator.Evaluate(input);
        var second = PortfolioRiskEvaluator.Evaluate(input with
        {
            EvaluatedAtUtc = input.EvaluatedAtUtc.AddMinutes(5)
        });
        Equal(first.RiskSnapshotUid, second.RiskSnapshotUid);
    }

    private static void RestrictedBudgetMultiplier()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new DeterministicRiskDecisionEngine(Options.Create(new DeterministicRiskOptions()));
        var normal = engine.Evaluate(Request(now, PortfolioRiskContractV1.Normal, 1m, true));
        var restricted = engine.Evaluate(Request(now, PortfolioRiskContractV1.Restricted, 0.5m, true));

        Equal(RiskDecisionContractV1.Approved, normal.Decision);
        Equal(RiskDecisionContractV1.Approved, restricted.Decision);
        NotNull(normal.Budget);
        NotNull(restricted.Budget);
        Equal(normal.Budget!.MaximumRiskAmount / 2m, restricted.Budget!.MaximumRiskAmount);
        Equal(normal.Budget.MaximumCapitalAllocation / 2m, restricted.Budget.MaximumCapitalAllocation);
    }

    private static void HardModeBlocksNewExposure()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new DeterministicRiskDecisionEngine(Options.Create(new DeterministicRiskOptions()));
        var decision = engine.Evaluate(Request(now, PortfolioRiskContractV1.CloseOnly, 0m, false));
        Equal(RiskDecisionContractV1.Rejected, decision.Decision);
        Contains(decision.Reasons, "PORTFOLIO_OPERATING_MODE_ALLOWED");
        Contains(decision.Reasons, "PORTFOLIO_RISK_MULTIPLIER_VALID");
    }

    private static PortfolioRiskEvaluationInputV1 Input(
        decimal dailyPnl,
        decimal? weeklyPnl = null,
        decimal strategyDrawdown = 0.02m,
        decimal portfolioDrawdown = 0.03m)
    {
        var now = new DateTimeOffset(2026, 7, 2, 9, 15, 0, TimeSpan.Zero);
        return new PortfolioRiskEvaluationInputV1(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "PRIMARY-PAPER",
            "PAPER",
            "INR",
            dailyPnl,
            weeklyPnl ?? dailyPnl,
            100_000m,
            strategyDrawdown,
            portfolioDrawdown,
            now,
            now,
            new PortfolioRiskPolicyV1(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "risk-policy-v1.0.0",
                "PAPER",
                0.01m,
                0.02m,
                0.03m,
                0.10m,
                0.12m,
                0.5m,
                PortfolioRiskContractV1.CloseOnly));
    }

    private static RiskDecisionRequestV1 Request(
        DateTimeOffset now,
        string mode,
        decimal multiplier,
        bool newExposureAllowed)
    {
        return new RiskDecisionRequestV1(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            new CanonicalCandidateSignalV1(
                Guid.NewGuid(),
                ThesisFusionContractV1.CandidateStatus,
                "NSE:NIFTY50-INDEX",
                EvidenceDirectionV1.Long,
                "5m",
                80m,
                80m,
                now,
                "fusion-policy-v1.0.0",
                Guid.NewGuid()),
            new PortfolioRiskSnapshotV1(
                "PRIMARY-PAPER",
                "PAPER",
                100_000m,
                80_000m,
                10_000m,
                10_000m,
                0m,
                0m,
                1m,
                0,
                Array.Empty<PortfolioPositionV1>(),
                now,
                mode,
                multiplier,
                newExposureAllowed,
                Guid.NewGuid()),
            new OperationalRiskStateV1(
                false,
                false,
                true,
                true,
                true,
                true,
                now),
            "risk-policy-v1.0.0",
            now);
    }

    private static void Run(List<string> failures, string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void True(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value)
            throw new InvalidOperationException("Expected false.");
    }

    private static void NotNull(object? value)
    {
        if (value is null)
            throw new InvalidOperationException("Expected non-null value.");
    }

    private static void Contains(IEnumerable<string> values, string expected)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }
}
