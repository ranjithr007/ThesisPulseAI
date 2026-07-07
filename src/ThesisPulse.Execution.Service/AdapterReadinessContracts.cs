namespace ThesisPulse.Execution.Service;

public static class AdapterReadinessContractV1
{
    public const string ContractVersion = "adapter-readiness.v1";

    public const string StatusReady = "READY";

    public const string StatusNotReady = "NOT_READY";

    public const string StatusDisabled = "DISABLED";

    public const string CheckPass = "PASS";

    public const string CheckFail = "FAIL";

    public const string CheckNotEvaluated = "NOT_EVALUATED";
}

public sealed record AdapterReadinessStatusV1(
    string ContractVersion,
    string EvidenceUid,
    DateTimeOffset ObservedAtUtc,
    string AdapterName,
    string BoundaryMode,
    string ReadinessVersion,
    bool Enabled,
    string OverallStatus,
    IReadOnlyList<string> CanonicalInstruments,
    IReadOnlyList<AdapterReadinessCheckV1> Checks);

public sealed record AdapterReadinessCheckV1(
    string Name,
    string Status,
    string Detail);
