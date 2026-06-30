using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
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
                reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7))
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
                reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12),
                reader.GetString(13), reader.GetInt32(14),
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
                reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetInt32(7), reader.GetInt64(8),
                ReadUtc(reader, 9)));
        }

        return rows;
    }
}
