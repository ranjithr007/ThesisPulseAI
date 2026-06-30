namespace ThesisPulse.Risk.Service;

public sealed class DeterministicTradePlanOptions
{
    public const string SectionName = "TradePlanBuilder";

    public string BuilderVersion { get; init; } = "deterministic-trade-plan-v1.0.0";
    public string ExecutionPolicyVersion { get; init; } = "execution-policy-v1.0.0";
    public List<string> AllowedEnvironments { get; init; } = ["PAPER"];
    public List<string> AllowedPositionIntents { get; init; } = ["INTRADAY"];
    public decimal MaximumSlippageFraction { get; init; } = 0.0025m;
    public decimal MinimumFirstTargetRiskReward { get; init; } = 1.5m;
    public int MaximumPlanValiditySeconds { get; init; } = 60;
}
