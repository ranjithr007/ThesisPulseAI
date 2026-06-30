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
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = request.PortfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FillRow(
            reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            $"{reader.GetString(9)}|{reader.GetString(10)}",
            reader.GetString(11), reader.GetString(12), reader.GetDecimal(13),
            reader.GetDecimal(14), reader.GetDecimal(15), reader.GetDecimal(16),
            ReadUtc(reader, 17), reader.GetGuid(18));
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
                    EvidenceDirectionV1.Neutral, 0m, null, 0m, 0m, 0m, 0m, 0),
                Array.Empty<PositionLotState>(),
                null);
        }

        var positionId = reader.GetInt64(0);
        var row = new PositionRow(
            positionId,
            reader.GetGuid(1),
            new PositionAccountingState(
                ParseDirection(reader.GetString(2)),
                reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.GetDecimal(8), reader.GetInt32(9)),
            Array.Empty<PositionLotState>(),
            reader.IsDBNull(10) ? null : ReadUtc(reader, 10));
        await reader.DisposeAsync();
        return row with
        {
            Lots = await ReadLotsAsync(connection, transaction, positionId, cancellationToken),
        };
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
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2),
                ParseDirection(reader.GetString(3)), reader.GetDecimal(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.GetDecimal(8), ReadUtc(reader, 9)));
        }

        return lots;
    }
}
