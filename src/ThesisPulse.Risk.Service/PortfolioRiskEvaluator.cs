using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public static class PortfolioRiskEvaluator
{
    public static PortfolioRiskSnapshotV1 Evaluate(PortfolioRiskEvaluationInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        var capitalBase = input.NetLiquidationValueAmount;
        var dailyLossFraction = LossFraction(input.DailyPnlAmount, capitalBase);
        var weeklyLossFraction = LossFraction(input.WeeklyPnlAmount, capitalBase);

        var reasons = new List<string>();
        var hardLimitBreached = false;

        if (dailyLossFraction >= input.Policy.DailyLossLimitFraction)
        {
            hardLimitBreached = true;
            reasons.Add("DAILY_LOSS_LIMIT_BREACHED");
        }

        if (weeklyLossFraction >= input.Policy.WeeklyLossLimitFraction)
        {
            hardLimitBreached = true;
            reasons.Add("WEEKLY_LOSS_LIMIT_BREACHED");
        }

        if (input.StrategyDrawdownFraction >= input.Policy.StrategyDrawdownLimitFraction)
        {
            hardLimitBreached = true;
            reasons.Add("STRATEGY_DRAWDOWN_LIMIT_BREACHED");
        }

        if (input.PortfolioDrawdownFraction >= input.Policy.PortfolioDrawdownLimitFraction)
        {
            hardLimitBreached = true;
            reasons.Add("PORTFOLIO_DRAWDOWN_LIMIT_BREACHED");
        }

        string operatingMode;
        decimal multiplier;

        if (hardLimitBreached)
        {
            operatingMode = input.Policy.LimitOperatingMode;
            multiplier = 0m;
        }
        else if (dailyLossFraction >= input.Policy.DailySoftLossFraction)
        {
            operatingMode = PortfolioRiskContractV1.Restricted;
            multiplier = input.Policy.RestrictedRiskMultiplier;
            reasons.Add("DAILY_SOFT_LOSS_LIMIT_REACHED");
        }
        else
        {
            operatingMode = PortfolioRiskContractV1.Normal;
            multiplier = 1m;
            reasons.Add("RISK_LIMITS_CLEAR");
        }

        var newExposureAllowed = operatingMode is PortfolioRiskContractV1.Normal or PortfolioRiskContractV1.Restricted;

        return new PortfolioRiskSnapshotV1(
            RiskSnapshotUid: CreateDeterministicSnapshotUid(input),
            SourcePnlSnapshotUid: input.PnlSnapshotUid,
            PolicyUid: input.Policy.PolicyUid,
            PolicyVersion: input.Policy.PolicyVersion,
            PortfolioCode: input.PortfolioCode,
            Environment: input.Environment,
            CurrencyCode: input.CurrencyCode,
            OperatingMode: operatingMode,
            EffectiveRiskMultiplier: multiplier,
            DailyPnlAmount: input.DailyPnlAmount,
            WeeklyPnlAmount: input.WeeklyPnlAmount,
            DailyLossFraction: dailyLossFraction,
            WeeklyLossFraction: weeklyLossFraction,
            StrategyDrawdownFraction: input.StrategyDrawdownFraction,
            PortfolioDrawdownFraction: input.PortfolioDrawdownFraction,
            NewExposureAllowed: newExposureAllowed,
            RiskReducingExitAllowed: true,
            Reasons: reasons,
            SourceAsOfUtc: input.SourceAsOfUtc,
            EvaluatedAtUtc: input.EvaluatedAtUtc);
    }

    private static decimal LossFraction(decimal pnlAmount, decimal capitalBase) =>
        pnlAmount >= 0m ? 0m : decimal.Abs(pnlAmount) / capitalBase;

    private static Guid CreateDeterministicSnapshotUid(PortfolioRiskEvaluationInputV1 input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{input.PnlSnapshotUid:N}|{input.Policy.PolicyUid:N}|{input.Policy.PolicyVersion}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void Validate(PortfolioRiskEvaluationInputV1 input)
    {
        if (input.PnlSnapshotUid == Guid.Empty)
            throw new ArgumentException("P&L snapshot identity is required.", nameof(input));
        if (input.Policy.PolicyUid == Guid.Empty)
            throw new ArgumentException("Risk policy identity is required.", nameof(input));
        if (input.NetLiquidationValueAmount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(input), "Net liquidation value must be positive.");
        if (!string.Equals(input.Environment, input.Policy.Environment, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Risk policy environment must match the portfolio snapshot environment.", nameof(input));
        if (input.Policy.DailySoftLossFraction < 0m ||
            input.Policy.DailyLossLimitFraction <= input.Policy.DailySoftLossFraction ||
            input.Policy.WeeklyLossLimitFraction <= 0m ||
            input.Policy.StrategyDrawdownLimitFraction <= 0m ||
            input.Policy.PortfolioDrawdownLimitFraction <= 0m)
            throw new ArgumentException("Risk policy limits are invalid.", nameof(input));
        if (input.Policy.RestrictedRiskMultiplier is <= 0m or > 1m)
            throw new ArgumentException("Restricted risk multiplier must be greater than zero and no more than one.", nameof(input));
        if (input.Policy.LimitOperatingMode is not (
            PortfolioRiskContractV1.CloseOnly or
            PortfolioRiskContractV1.Paused or
            PortfolioRiskContractV1.Halted))
            throw new ArgumentException("Limit operating mode must fail closed.", nameof(input));
    }
}
