using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Contracts.Portfolio.V1;

public static class PortfolioLedgerContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Projected = "PROJECTED";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected = "REJECTED";
    public const string Reconciled = "RECONCILED";
    public const string Discrepant = "DISCREPANT";
}

public sealed record PortfolioFillProjectionRequestV1(
    Guid RequestUid,
    Guid FillUid,
    string PortfolioCode,
    string CorrelationId,
    DateTimeOffset AsOfUtc);

public sealed record PositionLedgerSnapshotV1(
    Guid PositionUid,
    string PortfolioCode,
    string Environment,
    string InstrumentKey,
    string ProductType,
    EvidenceDirectionV1 Direction,
    decimal Quantity,
    decimal? AverageOpenPrice,
    decimal CostBasisAmount,
    decimal MarketValueAmount,
    decimal RealizedPnlAmount,
    decimal UnrealizedPnlAmount,
    decimal AccruedFeesAmount,
    decimal AccruedTaxesAmount,
    string Status,
    int Version,
    DateTimeOffset? OpenedAtUtc,
    DateTimeOffset? LastFillAtUtc,
    DateTimeOffset? ClosedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CashLedgerSnapshotV1(
    string PortfolioCode,
    string CurrencyCode,
    decimal SettledAmount,
    decimal UnsettledReceivableAmount,
    decimal UnsettledPayableAmount,
    decimal ReservedAmount,
    decimal TotalBalanceAmount,
    decimal AvailableAmount,
    int Version,
    long LastLedgerSequence,
    DateTimeOffset AsOfUtc);

public sealed record PortfolioLedgerSnapshotV1(
    Guid PortfolioUid,
    string PortfolioCode,
    string Environment,
    string AccountingMethod,
    string CurrencyCode,
    IReadOnlyCollection<PositionLedgerSnapshotV1> Positions,
    IReadOnlyCollection<CashLedgerSnapshotV1> CashBalances,
    decimal GrossExposureAmount,
    decimal NetExposureAmount,
    decimal RealizedPnlAmount,
    decimal UnrealizedPnlAmount,
    int OpenPositionCount,
    DateTimeOffset AsOfUtc);

public sealed record PortfolioFillProjectionResultV1(
    Guid RequestUid,
    Guid FillUid,
    string Status,
    IReadOnlyCollection<string> Reasons,
    PositionLedgerSnapshotV1? Position,
    PortfolioLedgerSnapshotV1? Portfolio,
    DateTimeOffset EvaluatedAtUtc);

public sealed record LedgerReconciliationRequestV1(
    Guid RequestUid,
    string PortfolioCode,
    string CorrelationId,
    string TriggerType,
    DateTimeOffset AsOfUtc);

public sealed record LedgerDiscrepancyV1(
    Guid DiscrepancyUid,
    string Type,
    string Severity,
    string ScopeReference,
    string Description,
    decimal? ExpectedQuantity,
    decimal? ActualQuantity,
    decimal? ExpectedAmount,
    decimal? ActualAmount,
    bool BlocksNewExposure,
    bool AllowsRiskReducingExits);

public sealed record LedgerReconciliationResultV1(
    Guid RequestUid,
    Guid ReconciliationRunUid,
    string PortfolioCode,
    string Status,
    int ObservationCount,
    IReadOnlyCollection<LedgerDiscrepancyV1> Discrepancies,
    bool BlocksNewExposure,
    bool AllowsRiskReducingExits,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
