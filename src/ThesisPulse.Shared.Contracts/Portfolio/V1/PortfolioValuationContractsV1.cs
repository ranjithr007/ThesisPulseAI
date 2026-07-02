namespace ThesisPulse.Shared.Contracts.Portfolio.V1;

public static class PortfolioValuationContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string Valued = "VALUED";
    public const string Duplicate = "DUPLICATE";
    public const string Deferred = "DEFERRED";
    public const string Rejected = "REJECTED";
}

public sealed record PositionPnlSnapshotV1(
    Guid PositionUid,
    string InstrumentKey,
    string ProductType,
    string Direction,
    decimal Quantity,
    decimal AverageOpenPrice,
    decimal MarkPrice,
    decimal MarketValueAmount,
    decimal RealizedPnlAmount,
    decimal UnrealizedPnlAmount,
    decimal FeesAmount,
    decimal TaxesAmount,
    decimal NetPnlAmount,
    decimal GrossExposureAmount,
    decimal NetExposureAmount,
    DateTimeOffset ValuedAtUtc);

public sealed record PortfolioPnlSnapshotV1(
    Guid PnlSnapshotUid,
    string PortfolioCode,
    string Environment,
    string CurrencyCode,
    decimal RealizedPnlAmount,
    decimal UnrealizedPnlAmount,
    decimal GrossPnlAmount,
    decimal FeesAmount,
    decimal TaxesAmount,
    decimal NetPnlAmount,
    decimal GrossExposureAmount,
    decimal NetExposureAmount,
    decimal CashBalanceAmount,
    decimal NetLiquidationValueAmount,
    decimal StrategyDrawdownFraction,
    decimal PortfolioDrawdownFraction,
    DateTimeOffset AsOfUtc,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyCollection<PositionPnlSnapshotV1> Positions);

public sealed record PortfolioValuationPersistenceResultV1(
    Guid RequestUid,
    string Status,
    IReadOnlyCollection<string> Reasons,
    PortfolioPnlSnapshotV1? Snapshot,
    DateTimeOffset EvaluatedAtUtc);
