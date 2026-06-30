using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    public async Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectionRequest(request);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var fill = await ReadFillAsync(connection, transaction, request, cancellationToken);
            if (fill is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ProjectionRejected(request, "FILL_OR_PORTFOLIO_NOT_FOUND");
            }

            if (await IsFillProjectedAsync(
                    connection,
                    transaction,
                    fill.FillId,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return await BuildProjectionResultAsync(
                    request,
                    fill,
                    PortfolioLedgerContractV1.Duplicate,
                    cancellationToken);
            }

            var current = await ReadPositionAsync(
                connection,
                transaction,
                fill,
                cancellationToken);
            var accounting = DeterministicPositionAccounting.ApplyFill(
                current.State,
                current.Lots,
                new PositionFillInput(
                    fill.FillUid,
                    fill.Side,
                    fill.Quantity,
                    fill.Price,
                    fill.FeesAmount,
                    fill.TaxesAmount,
                    fill.FillAtUtc));
            var positionId = await UpsertPositionAsync(
                connection,
                transaction,
                fill,
                current,
                accounting,
                cancellationToken);
            var eventId = await InsertPositionEventAsync(
                connection,
                transaction,
                fill,
                positionId,
                accounting,
                cancellationToken);
            await ApplyLotsAsync(
                connection,
                transaction,
                fill,
                positionId,
                eventId,
                accounting,
                cancellationToken);
            await ApplyCashAsync(
                connection,
                transaction,
                fill,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return await BuildProjectionResultAsync(
                request,
                fill,
                PortfolioLedgerContractV1.Projected,
                cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var snapshot = await GetSnapshotAsync(
                request.PortfolioCode,
                request.AsOfUtc,
                cancellationToken);
            return new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Duplicate,
                Array.Empty<string>(),
                snapshot?.Positions.FirstOrDefault(),
                snapshot,
                request.AsOfUtc);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PortfolioLedgerSnapshotV1?> GetSnapshotAsync(
        string portfolioCode,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioCode);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var portfolio = await ReadPortfolioAsync(
            connection,
            null,
            portfolioCode,
            false,
            cancellationToken);
        if (portfolio is null)
        {
            return null;
        }

        var positions = await ReadPositionSnapshotsAsync(
            connection,
            portfolio,
            cancellationToken);
        var cash = await ReadCashSnapshotsAsync(
            connection,
            portfolio,
            cancellationToken);
        var gross = positions.Sum(position => position.MarketValueAmount);
        var net = positions.Sum(position => position.Direction switch
        {
            EvidenceDirectionV1.Long => position.MarketValueAmount,
            EvidenceDirectionV1.Short => -position.MarketValueAmount,
            _ => 0m,
        });

        return new PortfolioLedgerSnapshotV1(
            portfolio.PortfolioUid,
            portfolio.PortfolioCode,
            portfolio.Environment,
            portfolio.AccountingMethod,
            portfolio.CurrencyCode,
            positions,
            cash,
            gross,
            net,
            positions.Sum(position => position.RealizedPnlAmount),
            positions.Sum(position => position.UnrealizedPnlAmount),
            positions.Count(position => position.Status == "OPEN"),
            asOfUtc);
    }

    public async Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateReconciliationRequest(request);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var portfolio = await ReadPortfolioAsync(
                connection,
                transaction,
                request.PortfolioCode,
                true,
                cancellationToken)
                ?? throw new InvalidOperationException("Portfolio was not found.");
            var discrepancies = await DetectDiscrepanciesAsync(
                connection,
                transaction,
                portfolio,
                cancellationToken);
            var runUid = Guid.NewGuid();
            var runId = await InsertReconciliationRunAsync(
                connection,
                transaction,
                request,
                portfolio,
                runUid,
                discrepancies,
                cancellationToken);
            await InsertReconciliationDiscrepanciesAsync(
                connection,
                transaction,
                runId,
                request.AsOfUtc,
                discrepancies,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new LedgerReconciliationResultV1(
                request.RequestUid,
                runUid,
                request.PortfolioCode,
                discrepancies.Count == 0
                    ? PortfolioLedgerContractV1.Reconciled
                    : PortfolioLedgerContractV1.Discrepant,
                discrepancies.Count + 3,
                discrepancies,
                discrepancies.Count > 0,
                true,
                request.AsOfUtc,
                request.AsOfUtc);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<PortfolioFillProjectionResultV1> BuildProjectionResultAsync(
        PortfolioFillProjectionRequestV1 request,
        FillRow fill,
        string status,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(
            request.PortfolioCode,
            request.AsOfUtc,
            cancellationToken);
        var position = snapshot?.Positions.FirstOrDefault(item =>
            string.Equals(item.InstrumentKey, fill.InstrumentKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ProductType, fill.ProductType, StringComparison.Ordinal));
        return new PortfolioFillProjectionResultV1(
            request.RequestUid,
            request.FillUid,
            status,
            Array.Empty<string>(),
            position,
            snapshot,
            request.AsOfUtc);
    }

    private static PortfolioFillProjectionResultV1 ProjectionRejected(
        PortfolioFillProjectionRequestV1 request,
        string reason) =>
        new(
            request.RequestUid,
            request.FillUid,
            PortfolioLedgerContractV1.Rejected,
            [reason],
            null,
            null,
            request.AsOfUtc);

    private static void ValidateProjectionRequest(PortfolioFillProjectionRequestV1 request)
    {
        if (request.RequestUid == Guid.Empty || request.FillUid == Guid.Empty)
        {
            throw new ArgumentException("Request and fill UIDs are required.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
    }

    private static void ValidateReconciliationRequest(LedgerReconciliationRequestV1 request)
    {
        if (request.RequestUid == Guid.Empty)
        {
            throw new ArgumentException("Request UID is required.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        var trigger = request.TriggerType?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!ReconciliationTriggers.Contains(trigger))
        {
            throw new ArgumentException("Unsupported reconciliation trigger.", nameof(request));
        }
    }
}
