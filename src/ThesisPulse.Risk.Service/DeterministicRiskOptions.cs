namespace ThesisPulse.Risk.Service;

public sealed class DeterministicRiskOptions
{
    public const string SectionName = "RiskDecision";

    public string EngineVersion { get; init; } = "deterministic-risk-v1.0.0";
    public string RiskPolicyVersion { get; init; } = "risk-policy-v1.0.0";
    public List<string> AllowedEnvironments { get; init; } = ["PAPER"];
    public int MaximumCandidateAgeSeconds { get; init; } = 120;
    public int MaximumSnapshotAgeSeconds { get; init; } = 60;
    public decimal MinimumCandidateStrength { get; init; } = 68m;
    public decimal MinimumCandidateConfidence { get; init; } = 65m;
    public decimal MaximumDailyLossPercent { get; init; } = 2m;
    public decimal MaximumDrawdownPercent { get; init; } = 8m;
    public decimal MaximumGrossExposurePercent { get; init; } = 100m;
    public decimal MaximumSinglePositionExposurePercent { get; init; } = 20m;
    public decimal MaximumRiskPerTradePercent { get; init; } = 1m;
    public int MaximumOpenPositions { get; init; } = 5;
    public bool AllowPyramiding { get; init; }
    public int BudgetValiditySeconds { get; init; } = 60;
}
