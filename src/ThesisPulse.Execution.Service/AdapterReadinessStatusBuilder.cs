namespace ThesisPulse.Execution.Service;

public static class AdapterReadinessStatusBuilder
{
    public static AdapterReadinessStatusV1 Build(AdapterReadinessOptions options)
    {
        var checks = new List<AdapterReadinessCheckV1>
        {
            new("adapter-name", Present(options.AdapterName), "bounded metadata"),
            new("adapter-boundary", BoundaryStatus(options.BoundaryMode), "bounded metadata"),
            new("configuration-shape", FlagStatus(options.ConfigurationShapeAvailable), "bounded metadata"),
            new("instrument-mapping", FlagStatus(options.InstrumentMappingAvailable), "bounded metadata"),
            new("market-calendar", FlagStatus(options.MarketCalendarAvailable), "bounded metadata"),
            new("canonical-instruments", RequiredInstrumentStatus(options.CanonicalInstruments), "bounded metadata"),
        };

        var hasFailed = checks.Any(check => check.Status == AdapterReadinessContractV1.CheckFail);
        var hasPending = checks.Any(check => check.Status == AdapterReadinessContractV1.CheckNotEvaluated);
        var overallStatus = options.Enabled switch
        {
            false => AdapterReadinessContractV1.StatusDisabled,
            true when hasFailed || hasPending => AdapterReadinessContractV1.StatusNotReady,
            _ => AdapterReadinessContractV1.StatusReady,
        };

        return new AdapterReadinessStatusV1(
            ContractVersion: AdapterReadinessContractV1.ContractVersion,
            EvidenceUid: CreateEvidenceUid(options),
            ObservedAtUtc: DateTimeOffset.UtcNow,
            AdapterName: options.AdapterName,
            BoundaryMode: options.BoundaryMode,
            ReadinessVersion: options.ReadinessVersion,
            Enabled: options.Enabled,
            OverallStatus: overallStatus,
            CanonicalInstruments: options.CanonicalInstruments,
            Checks: checks);
    }

    private static string Present(string value) => string.IsNullOrWhiteSpace(value)
        ? AdapterReadinessContractV1.CheckFail
        : AdapterReadinessContractV1.CheckPass;

    private static string FlagStatus(bool value) => value
        ? AdapterReadinessContractV1.CheckPass
        : AdapterReadinessContractV1.CheckNotEvaluated;

    private static string BoundaryStatus(string value) =>
        string.Equals(value, AdapterBoundaryMode.ReadOnly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, AdapterBoundaryMode.DryRunReadiness, StringComparison.OrdinalIgnoreCase)
            ? AdapterReadinessContractV1.CheckPass
            : AdapterReadinessContractV1.CheckFail;

    private static string RequiredInstrumentStatus(IReadOnlyList<string> instruments) =>
        instruments.Any(instrument => string.Equals(instrument, "NIFTY 50", StringComparison.OrdinalIgnoreCase)) &&
        instruments.Any(instrument => string.Equals(instrument, "BANK NIFTY", StringComparison.OrdinalIgnoreCase)) &&
        instruments.Any(instrument => string.Equals(instrument, "FINNIFTY", StringComparison.OrdinalIgnoreCase))
            ? AdapterReadinessContractV1.CheckPass
            : AdapterReadinessContractV1.CheckFail;

    private static string CreateEvidenceUid(AdapterReadinessOptions options) =>
        string.Join(
            ':',
            "adapter",
            options.AdapterName.Trim().ToUpperInvariant(),
            options.ReadinessVersion.Trim().ToUpperInvariant(),
            options.CanonicalInstruments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
