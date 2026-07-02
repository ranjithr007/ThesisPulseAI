namespace ThesisPulse.Shared.Contracts.Risk.V1;

public static class PortfolioRiskContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Normal = "NORMAL";
    public const string Restricted = "RESTRICTED";
    public const string CloseOnly = "CLOSE_ONLY";
    public const string Paused = "PAUSED";
    public const string Halted = "HALTED";
}

public sealed record PortfolioRiskPolicyV1(
    Guid PolicyUid,
    string PolicyVersion,
    string Environment,
    decimal DailySoftLossFraction,
    decimal DailyLossLimitFraction,
    decimal WeeklyLossLimitFraction,
    decimal StrategyDrawdownLimitFraction,
    decimal PortfolioDrawdownLimitFraction,
    decimal RestrictedRiskMultiplier,
    string LimitOperatingMode);

public sealed record PortfolioRiskEvaluationInputV1(
    Guid PnlSnapshotUid,
    string PortfolioCode,
    string Environment,
    string CurrencyCode,
    decimal DailyPnlAmount,
    decimal WeeklyPnlAmount,
    decimal NetLiquidationValueAmount,
    decimal StrategyDrawdownFraction,
    decimal PortfolioDrawdownFraction,
    DateTimeOffset SourceAsOfUtc,
    DateTimeOffset EvaluatedAtUtc,
    PortfolioRiskPolicyV1 Policy);

public sealed record PortfolioRiskStateSnapshotV1(
    Guid RiskSnapshotUid,
    Guid SourcePnlSnapshotUid,
    Guid PolicyUid,
    string PolicyVersion,
    string PortfolioCode,
    string Environment,
    string CurrencyCode,
    string OperatingMode,
    decimal EffectiveRiskMultiplier,
    decimal DailyPnlAmount,
    decimal WeeklyPnlAmount,
    decimal DailyLossFraction,
    decimal WeeklyLossFraction,
    decimal StrategyDrawdownFraction,
    decimal PortfolioDrawdownFraction,
    bool NewExposureAllowed,
    bool RiskReducingExitAllowed,
    IReadOnlyCollection<string> Reasons,
    DateTimeOffset SourceAsOfUtc,
    DateTimeOffset EvaluatedAtUtc);
