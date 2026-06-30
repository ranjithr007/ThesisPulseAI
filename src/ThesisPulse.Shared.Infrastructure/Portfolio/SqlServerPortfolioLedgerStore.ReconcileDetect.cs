using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    private async Task<IReadOnlyCollection<LedgerDiscrepancyV1>> DetectDiscrepanciesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRow portfolio,
        CancellationToken cancellationToken)
    {
        var rows = new List<LedgerDiscrepancyV1>();
        await DetectUnprojectedFillsAsync(connection, transaction, portfolio, rows, cancellationToken);
        await DetectPositionMismatchesAsync(connection, transaction, portfolio, rows, cancellationToken);
        await DetectCashMismatchesAsync(connection, transaction, portfolio, rows, cancellationToken);
        return rows;
    }

    private async Task DetectUnprojectedFillsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRow portfolio,
        ICollection<LedgerDiscrepancyV1> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT f.[fill_uid], e.[exchange_code], i.[canonical_symbol], f.[fill_quantity]
            FROM [execution].[fills] f
            INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id] = f.[trade_plan_id]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = f.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            LEFT JOIN [portfolio].[position_events] pe ON pe.[fill_id] = f.[fill_id]
            WHERE f.[broker_account_id] = @account_id
              AND f.[environment] = 'PAPER'
              AND s.[strategy_code] = @strategy_code
              AND pe.[position_event_id] IS NULL;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@account_id", SqlDbType.BigInt).Value = portfolio.BrokerAccountId;
        command.Parameters.Add("@strategy_code", SqlDbType.VarChar, 100).Value = portfolio.StrategyCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerDiscrepancyV1(
                Guid.NewGuid(),
                "FILL_MISMATCH",
                "HIGH",
                reader.GetGuid(0).ToString("D"),
                $"Fill for {reader.GetString(1)}|{reader.GetString(2)} has no position event.",
                reader.GetDecimal(3),
                0m,
                null,
                null,
                true,
                true));
        }
    }

    private async Task DetectPositionMismatchesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRow portfolio,
        ICollection<LedgerDiscrepancyV1> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                p.[position_uid], e.[exchange_code], i.[canonical_symbol],
                p.[quantity], COALESCE(SUM(l.[remaining_quantity]), 0)
            FROM [portfolio].[positions] p
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = p.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            LEFT JOIN [portfolio].[position_lots] l ON l.[position_id] = p.[position_id]
            WHERE p.[portfolio_id] = @portfolio_id
            GROUP BY p.[position_uid], e.[exchange_code], i.[canonical_symbol], p.[quantity]
            HAVING p.[quantity] <> COALESCE(SUM(l.[remaining_quantity]), 0);
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerDiscrepancyV1(
                Guid.NewGuid(),
                "POSITION_MISMATCH",
                "HIGH",
                reader.GetGuid(0).ToString("D"),
                $"Position for {reader.GetString(1)}|{reader.GetString(2)} differs from FIFO lots.",
                reader.GetDecimal(4),
                reader.GetDecimal(3),
                null,
                null,
                true,
                true));
        }
    }

    private async Task DetectCashMismatchesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRow portfolio,
        ICollection<LedgerDiscrepancyV1> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [currency_code], [total_balance_amount],
                [settled_amount] + [unsettled_receivable_amount] - [unsettled_payable_amount]
            FROM [portfolio].[cash_balances]
            WHERE [portfolio_id] = @portfolio_id
              AND
              (
                  [total_balance_amount] <>
                      [settled_amount] + [unsettled_receivable_amount] - [unsettled_payable_amount]
                  OR [available_amount] <> [total_balance_amount] - [reserved_amount]
              );
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerDiscrepancyV1(
                Guid.NewGuid(),
                "FUNDS_MISMATCH",
                "CRITICAL",
                reader.GetString(0),
                "Cash balance identity is inconsistent.",
                null,
                null,
                reader.GetDecimal(2),
                reader.GetDecimal(1),
                true,
                true));
        }
    }
}
