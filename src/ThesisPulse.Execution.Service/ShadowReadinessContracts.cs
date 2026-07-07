namespace ThesisPulse.Execution.Service;

public static class ShadowReadinessContractV1
{
    public const string ContractVersion = "shadow-readiness.v1";

    public const string StatusReady = "READY";

    public const string StatusDisabled = "DISABLED";

    public const string StatusNotReady = "NOT_READY";

    public const string CheckPass = "PASS";

    public const string CheckFail = "FAIL";

    public const string CheckNotEvaluated = "NOT_EVALUATED";
}

public sealed record ShadowReadinessStatusV1(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    string ReadinessVersion,
    string Environment,
    string Mode,
    string BrokerAdapter,
    bool Enabled,
    string OverallStatus,
    IReadOnlyList<ShadowReadinessCheckV1> Checks,
    ShadowReadinessAuthorityV1 Authority);

public sealed record ShadowReadinessCheckV1(
    string Name,
    string Status,
    string Detail);

public sealed record ShadowReadinessAuthorityV1(
    bool BrokerOrderSubmission,
    bool BrokerOrderModification,
    bool BrokerOrderCancellation,
    bool PortfolioMutation,
    bool RiskOverride,
    bool LiveExecution);
