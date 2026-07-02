namespace ThesisPulse.Shared.Contracts.Risk.V1;

public static class PortfolioRiskControlContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Normal = "NORMAL";
    public const string Restricted = "RESTRICTED";
    public const string CloseOnly = "CLOSE_ONLY";
    public const string Paused = "PAUSED";
    public const string Halted = "HALTED";

    public static bool BlocksNewExposure(string operatingMode) =>
        operatingMode is CloseOnly or Paused or Halted;
}

public sealed record PortfolioRiskControlStateV1(
    Guid StateUid,
    Guid PortfolioSnapshotUid,
    Guid SourcePnlSnapshotUid,
    Guid RiskPolicyUid,
    string RiskPolicyVersion,
    string PortfolioCode,
    string Environment,
    string OperatingMode,
    bool AllowsNewExposure,
    bool AllowsRiskReducingExits,
    decimal RiskMultiplier,
    int MaximumConcurrentNewPositions,
    decimal EquityAmount,
    decimal DailyPnlAmount,
    decimal WeeklyPnlAmount,
    decimal DailyLossFraction,
    decimal WeeklyLossFraction,
    decimal StrategyDrawdownFraction,
    decimal PortfolioDrawdownFraction,
    IReadOnlyCollection<string> ReasonCodes,
    DateTimeOffset AsOfUtc,
    DateTimeOffset EvaluatedAtUtc);

public sealed record PortfolioRiskProjectionResultV1(
    Guid RequestUid,
    string Status,
    IReadOnlyCollection<string> Reasons,
    PortfolioRiskControlStateV1? State,
    DateTimeOffset EvaluatedAtUtc);
