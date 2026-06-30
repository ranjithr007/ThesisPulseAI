using ThesisPulse.Shared.Contracts.Portfolio.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public interface IPortfolioLedgerStore
{
    Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken = default);

    Task<PortfolioLedgerSnapshotV1?> GetSnapshotAsync(
        string portfolioCode,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        CancellationToken cancellationToken = default);
}

public sealed record PortfolioFillRecord(
    Guid FillUid,
    Guid OrderUid,
    string Environment,
    string InstrumentKey,
    string ProductType,
    string Side,
    decimal Quantity,
    decimal Price,
    decimal FeesAmount,
    decimal TaxesAmount,
    string CurrencyCode,
    DateTimeOffset FilledAtUtc,
    string CorrelationId);
