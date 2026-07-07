namespace ThesisPulse.Execution.Service;

public sealed class ShadowReadinessOptions
{
    public const string SectionName = "ShadowReadiness";

    public bool Enabled { get; init; }

    public string Mode { get; init; } = ShadowReadinessMode.ReadinessOnly;

    public string Environment { get; init; } = "SHADOW";

    public string BrokerAdapter { get; init; } = "Upstox";

    public string ReadinessVersion { get; init; } = "phase70-shadow-readiness-v1";

    public bool RequireBrokerConfigurationCheck { get; init; } = true;

    public bool RequireInstrumentMappingCheck { get; init; } = true;

    public bool RequireMarketSessionCheck { get; init; } = true;

    public bool AllowBrokerOrderSubmission { get; init; }

    public bool AllowBrokerOrderModification { get; init; }

    public bool AllowBrokerOrderCancellation { get; init; }

    public bool AllowPortfolioMutation { get; init; }

    public IReadOnlyList<string> InstrumentUniverse { get; init; } =
        ["NIFTY 50", "BANK NIFTY", "FINNIFTY"];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Mode))
        {
            throw new InvalidOperationException("SHADOW readiness mode is required.");
        }

        if (!ShadowReadinessMode.IsSupported(Mode))
        {
            throw new InvalidOperationException(
                $"Unsupported SHADOW readiness mode '{Mode}'.");
        }

        if (!string.Equals(Environment, "SHADOW", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Phase 7.0 SHADOW readiness status must use SHADOW environment metadata.");
        }

        if (string.IsNullOrWhiteSpace(BrokerAdapter))
        {
            throw new InvalidOperationException("Broker adapter name is required for SHADOW readiness.");
        }

        if (string.IsNullOrWhiteSpace(ReadinessVersion))
        {
            throw new InvalidOperationException("SHADOW readiness version is required.");
        }

        if (InstrumentUniverse.Count == 0)
        {
            throw new InvalidOperationException("At least one canonical instrument is required for SHADOW readiness.");
        }

        if (AllowBrokerOrderSubmission ||
            AllowBrokerOrderModification ||
            AllowBrokerOrderCancellation ||
            AllowPortfolioMutation)
        {
            throw new InvalidOperationException(
                "Phase 7.0 SHADOW readiness is non-executing and cannot enable broker order or portfolio mutation authority.");
        }
    }
}

public static class ShadowReadinessMode
{
    public const string ReadinessOnly = "READINESS_ONLY";

    public const string DryRunNonExecuting = "DRY_RUN_NON_EXECUTING";

    public static bool IsSupported(string mode) =>
        string.Equals(mode, ReadinessOnly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, DryRunNonExecuting, StringComparison.OrdinalIgnoreCase);
}
