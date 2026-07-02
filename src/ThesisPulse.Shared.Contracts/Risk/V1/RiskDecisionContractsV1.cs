using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Contracts.Risk.V1;

public static class RiskDecisionContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

public sealed record PortfolioPositionV1(
    string InstrumentKey,
    EvidenceDirectionV1 Direction,
    decimal NotionalValue,
    DateTimeOffset OpenedAtUtc);

public sealed record PortfolioRiskSnapshotV1(
    string AccountKey,
    string Environment,
    decimal Equity,
    decimal AvailableCash,
    decimal GrossExposure,
    decimal NetExposure,
    decimal RealizedPnlToday,
    decimal UnrealizedPnlToday,
    decimal CurrentDrawdownPercent,
    int OpenPositionCount,
    IReadOnlyCollection<PortfolioPositionV1> Positions,
    DateTimeOffset ObservedAtUtc,
    string OperatingMode = PortfolioRiskContractV1.Normal,
    decimal EffectiveRiskMultiplier = 1m,
    bool NewExposureAllowed = true,
    Guid? PortfolioRiskSnapshotUid = null);

public sealed record OperationalRiskStateV1(
    bool KillSwitchActive,
    bool TradingHalted,
    bool MarketOpen,
    bool MarketDataHealthy,
    bool PortfolioStateHealthy,
    bool BrokerConnectivityHealthy,
    DateTimeOffset ObservedAtUtc);

public sealed record RiskDecisionRequestV1(
    Guid RequestUid,
    string CorrelationId,
    CanonicalCandidateSignalV1 Candidate,
    PortfolioRiskSnapshotV1 Portfolio,
    OperationalRiskStateV1 Operations,
    string RiskPolicyVersion,
    DateTimeOffset AsOfUtc);

public sealed record RiskCheckV1(
    string Code,
    bool Passed,
    decimal? ObservedValue,
    decimal? LimitValue,
    string Detail);

public sealed record RiskBudgetV1(
    decimal MaximumRiskAmount,
    decimal MaximumCapitalAllocation,
    decimal MaximumGrossExposurePercent,
    DateTimeOffset ExpiresAtUtc);

public sealed record RiskDecisionV1(
    Guid RiskDecisionUid,
    Guid RequestUid,
    string CorrelationId,
    Guid SignalUid,
    Guid ThesisUid,
    string InstrumentKey,
    string Environment,
    EvidenceDirectionV1 Direction,
    string Decision,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<RiskCheckV1> Checks,
    RiskBudgetV1? Budget,
    string RiskPolicyVersion,
    string EngineVersion,
    DateTimeOffset EvaluatedAtUtc);
