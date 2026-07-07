namespace ThesisPulse.Execution.Service;

public sealed class AdapterReadinessOptions
{
    public const string SectionName = "AdapterReadiness";

    public bool Enabled { get; init; }

    public string AdapterName { get; init; } = "Primary";

    public string BoundaryMode { get; init; } = AdapterBoundaryMode.ReadOnly;

    public string ReadinessVersion { get; init; } = "phase71-adapter-readiness-v1";

    public bool ConfigurationShapeAvailable { get; init; }

    public bool InstrumentMappingAvailable { get; init; }

    public bool MarketCalendarAvailable { get; init; }

    public IReadOnlyList<string> CanonicalInstruments { get; init; } =
        ["NIFTY 50", "BANK NIFTY", "FINNIFTY"];
}

public static class AdapterBoundaryMode
{
    public const string ReadOnly = "READ_ONLY";

    public const string DryRunReadiness = "DRY_RUN_READINESS";
}
