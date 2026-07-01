namespace ThesisPulse.Thesis.Service;

public sealed class DeterministicFusionOptions
{
    public const string SectionName = "ThesisFusion";

    public string EngineVersion { get; init; } = "deterministic-fusion-v1.0.0";
    public string WeightConfigurationVersion { get; init; } = "fusion-weights-v1.0.0";
    public decimal MinimumCandidateScore { get; init; } = 68m;
    public decimal MinimumCandidateConfidence { get; init; } = 65m;
    public decimal MinimumScoreSeparation { get; init; } = 12m;
    public decimal MinimumPrimaryTimeframeConfidence { get; init; } = 60m;
    public int MinimumDirectionalEngines { get; init; } = 4;
    public int MinimumConfirmingTimeframes { get; init; } = 2;
    public int MaximumInputAgeSeconds { get; init; } = 420;
    public decimal HigherTimeframeVetoConfidence { get; init; } = 75m;

    public Dictionary<string, decimal> EngineWeights { get; init; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["TREND"] = 0.20m,
            ["MOMENTUM"] = 0.15m,
            ["ORDER_FLOW"] = 0.15m,
            ["SMART_MONEY_CONCEPTS"] = 0.15m,
            ["LIQUIDITY_DERIVATIVES_CONTEXT"] = 0.15m,
            ["OPTION_CHAIN"] = 0.20m,

            // Backward-compatible aliases retained for older recorded requests.
            ["SMC"] = 0.15m,
            ["LIQUIDITY"] = 0.10m,
            ["DERIVATIVES"] = 0.10m,
            ["WHALE_FLOW"] = 0.05m,
        };

    public Dictionary<string, decimal> TimeframeWeights { get; init; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = 0.10m,
            ["5m"] = 0.40m,
            ["15m"] = 0.25m,
            ["1h"] = 0.15m,
            ["1d"] = 0.10m,
            ["OPTION_CHAIN"] = 1.00m,
        };
}
