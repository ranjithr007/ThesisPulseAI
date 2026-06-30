using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    private async Task<FillRow?> ReadFillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                f.[fill_id], f.[fill_uid], f.[order_id], f.[broker_account_id],
                f.[instrument_id], p.[portfolio_id], p.[portfolio_code],
                p.[environment], p.[base_currency_code], e.[exchange_code],
                i.[canonical_symbol],
                CASE
                    WHEN o.[position_intent] = 'INTRADAY' THEN 'INTRADAY'
                    WHEN o.[position_intent] = 'DELIVERY' THEN 'DELIVERY'
                    ELSE 'FUTURE'
                END,
                f.[side], f.[fill_quantity], f.[fill_price],
                COALESCE(f.[fees_amount], 0), COALESCE(f.[taxes_amount], 0),
                f.[fill_at_utc], f.[correlation_id]
            FROM [execution].[fills] f WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [execution].[orders] o ON o.[order_id] = f.[order_id]
            INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id] = f.[trade_plan_id]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [portfolio].[portfolios] p WITH (UPDLOCK, HOLDLOCK)
                ON p.[broker_account_id] = f.[broker_account_id]
               AND p.[environment] = f.[environment]
               AND p.[strategy_code] = s.[strategy_code]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = f.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            WHERE f.[fill_uid] = @fill_uid
              AND p.[portfolio_code] = @portfolio_code
              AND p.[environment] = 'PAPER'
              AND p.[status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY');
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = request.FillUid;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
            request.PortfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FillRow(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            $"{reader.GetString(9)}|{reader.GetString(10)}",
            reader.GetString(11),
            reader.GetString(12),
            reader.GetDecimal(13),
            reader.GetDecimal(14),
            reader.GetDecimal(15),
            reader.GetDecimal(16),
            ReadUtc(reader, 17),
            reader.GetGuid(18));
    }

    private async Task<bool> IsFillProjectedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long fillId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) 1
            FROM [portfolio].[position_events] WITH (UPDLOCK, HOLDLOCK)
            WHERE [fill_id] = @fill_id;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fillId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<PositionRow> ReadPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                [position_id], [position_uid], [position_side], [quantity],
                [average_open_price], [cost_basis_amount], [realized_pnl_amount],
                [accrued_fees_amount], [accrued_taxes_amount],
                [current_position_version], [opened_at_utc]
            FROM [portfolio].[positions] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [instrument_id] = @instrument_id
              AND [product_type] = @product_type;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = fill.InstrumentId;
        command.Parameters.Add("@product_type", SqlDbType.VarChar, 30).Value = fill.ProductType;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PositionRow(
                null,
                Guid.NewGuid(),
                new PositionAccountingState(
                    EvidenceDirectionV1.Neutral,
                    0m,
                    null,
                    0m,
                    0m,
                    0m,
                    0m,
                    0),
                Array.Empty<PositionLotState>(),
                null);
        }

        var positionId = reader.GetInt64(0);
        var position = new PositionRow(
            positionId,
            reader.GetGuid(1),
            new PositionAccountingState(
                ParseDirection(reader.GetString(2)),
                reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetInt32(9)),
            Array.Empty<PositionLotState>(),
            reader.IsDBNull(10) ? null : ReadUtc(reader, 10));
        await reader.DisposeAsync();
        var lots = await ReadLotsAsync(
            connection,
            transaction,
            positionId,
            cancellationToken);
        return position with { Lots = lots };
    }

    private async Task<IReadOnlyCollection<PositionLotState>> ReadLotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long positionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [position_lot_id], [position_lot_uid], [lot_sequence], [lot_side],
                [opened_quantity], [remaining_quantity], [open_price],
                [allocated_open_fees_amount], [allocated_open_taxes_amount],
                [opened_at_utc]
            FROM [portfolio].[position_lots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [position_id] = @position_id
            ORDER BY [lot_sequence];
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lots = new List<PositionLotState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lots.Add(new PositionLotState(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                ParseDirection(reader.GetString(3)),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                ReadUtc(reader, 9)));
        }

        return lots;
    }

    private async Task<PortfolioRow?> ReadPortfolioAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string portfolioCode,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var lockHint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT
                [portfolio_id], [portfolio_uid], [portfolio_code], [environment],
                [broker_account_id], [strategy_code], [base_currency_code],
                [accounting_method]
            FROM [portfolio].[portfolios]{lockHint}
            WHERE [portfolio_code] = @portfolio_code
              AND [environment] = 'PAPER';
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = portfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PortfolioRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7))
            : null;
    }

    private async Task<IReadOnlyCollection<PositionLedgerSnapshotV1>> ReadPositionSnapshotsAsync(
        SqlConnection connection,
        PortfolioRow portfolio,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                p.[position_uid], e.[exchange_code], i.[canonical_symbol],
                p.[product_type], p.[position_side], p.[quantity],
                p.[average_open_price], p.[cost_basis_amount],
                p.[market_value_amount], p.[realized_pnl_amount],
                p.[unrealized_pnl_amount], p.[accrued_fees_amount],
                p.[accrued_taxes_amount], p.[status], p.[current_position_version],
                p.[opened_at_utc], p.[last_fill_at_utc], p.[closed_at_utc],
                p.[updated_at_utc]
            FROM [portfolio].[positions] p
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = p.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            WHERE p.[portfolio_id] = @portfolio_id
            ORDER BY e.[exchange_code], i.[canonical_symbol], p.[product_type];
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PositionLedgerSnapshotV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PositionLedgerSnapshotV1(
                reader.GetGuid(0),
                portfolio.PortfolioCode,
                portfolio.Environment,
                $"{reader.GetString(1)}|{reader.GetString(2)}",
                reader.GetString(3),
                ParseDirection(reader.GetString(4)),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetDecimal(12),
                reader.GetString(13),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : ReadUtc(reader, 15),
                reader.IsDBNull(16) ? null : ReadUtc(reader, 16),
                reader.IsDBNull(17) ? null : ReadUtc(reader, 17),
                ReadUtc(reader, 18)));
        }

        return rows;
    }

    private async Task<IReadOnlyCollection<CashLedgerSnapshotV1>> ReadCashSnapshotsAsync(
        SqlConnection connection,
        PortfolioRow portfolio,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [currency_code], [settled_amount], [unsettled_receivable_amount],
                [unsettled_payable_amount], [reserved_amount], [total_balance_amount],
                [available_amount], [current_balance_version], [last_ledger_sequence],
                [as_of_utc]
            FROM [portfolio].[cash_balances]
            WHERE [portfolio_id] = @portfolio_id
            ORDER BY [currency_code];
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CashLedgerSnapshotV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CashLedgerSnapshotV1(
                portfolio.PortfolioCode,
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetInt32(7),
                reader.GetInt64(8),
                ReadUtc(reader, 9)));
        }

        return rows;
    }
}
