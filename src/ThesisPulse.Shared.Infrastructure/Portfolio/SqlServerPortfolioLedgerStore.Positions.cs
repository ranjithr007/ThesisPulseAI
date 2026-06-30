using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    private async Task<long> UpsertPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        PositionRow before,
        PositionAccountingResult accounting,
        CancellationToken cancellationToken)
    {
        var status = accounting.After.Quantity == 0m ? "CLOSED" : "OPEN";
        var side = PositionSide(accounting.After.Direction);
        DateTimeOffset? openedAt = accounting.EventType == "REVERSED" || before.State.Quantity == 0m
            ? (accounting.After.Quantity > 0m ? fill.FillAtUtc : null)
            : before.OpenedAtUtc;
        DateTimeOffset? closedAt = accounting.After.Quantity == 0m
            ? fill.FillAtUtc
            : null;

        if (before.PositionId is null)
        {
            const string insertSql = """
                INSERT INTO [portfolio].[positions]
                (
                    [position_uid], [portfolio_id], [instrument_id], [product_type],
                    [position_side], [quantity], [average_open_price], [cost_basis_amount],
                    [market_value_amount], [realized_pnl_amount], [unrealized_pnl_amount],
                    [accrued_fees_amount], [accrued_taxes_amount],
                    [protective_exit_quantity], [status], [current_position_version],
                    [last_event_sequence], [opened_at_utc], [last_fill_at_utc],
                    [closed_at_utc], [last_valued_at_utc], [created_by], [updated_by]
                )
                OUTPUT INSERTED.[position_id]
                VALUES
                (
                    @position_uid, @portfolio_id, @instrument_id, @product_type,
                    @side, @quantity, @average_price, @cost_basis,
                    @market_value, @realized_pnl, @unrealized_pnl,
                    @fees, @taxes,
                    0, @status, @version,
                    @version, @opened_at_utc, @fill_at_utc,
                    @closed_at_utc, @fill_at_utc, @actor, @actor
                );
                """;
            await using var command = Command(connection, transaction, insertSql);
            AddPositionParameters(
                command,
                fill,
                before.PositionUid,
                accounting,
                side,
                status,
                openedAt,
                closedAt);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        const string updateSql = """
            UPDATE [portfolio].[positions]
            SET [position_side] = @side,
                [quantity] = @quantity,
                [average_open_price] = @average_price,
                [cost_basis_amount] = @cost_basis,
                [market_value_amount] = @market_value,
                [realized_pnl_amount] = @realized_pnl,
                [unrealized_pnl_amount] = @unrealized_pnl,
                [accrued_fees_amount] = @fees,
                [accrued_taxes_amount] = @taxes,
                [status] = @status,
                [current_position_version] = @version,
                [last_event_sequence] = @version,
                [opened_at_utc] = @opened_at_utc,
                [last_fill_at_utc] = @fill_at_utc,
                [closed_at_utc] = @closed_at_utc,
                [last_valued_at_utc] = @fill_at_utc,
                [updated_at_utc] = @fill_at_utc,
                [updated_by] = @actor
            WHERE [position_id] = @position_id
              AND [current_position_version] = @expected_version;
            """;
        await using var update = Command(connection, transaction, updateSql);
        AddPositionParameters(
            update,
            fill,
            before.PositionUid,
            accounting,
            side,
            status,
            openedAt,
            closedAt);
        update.Parameters.Add("@position_id", SqlDbType.BigInt).Value = before.PositionId.Value;
        update.Parameters.Add("@expected_version", SqlDbType.Int).Value = before.State.Version;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Position projection changed concurrently.");
        }

        return before.PositionId.Value;
    }

    private void AddPositionParameters(
        SqlCommand command,
        FillRow fill,
        Guid positionUid,
        PositionAccountingResult accounting,
        string side,
        string status,
        DateTimeOffset? openedAt,
        DateTimeOffset? closedAt)
    {
        command.Parameters.Add("@position_uid", SqlDbType.UniqueIdentifier).Value = positionUid;
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = fill.InstrumentId;
        command.Parameters.Add("@product_type", SqlDbType.VarChar, 30).Value = fill.ProductType;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = side;
        AddDecimal(command, "@quantity", accounting.After.Quantity);
        AddNullableDecimal(command, "@average_price", accounting.After.AverageOpenPrice);
        AddDecimal(command, "@cost_basis", accounting.After.CostBasisAmount);
        AddDecimal(command, "@market_value", accounting.MarketValueAmount);
        AddDecimal(command, "@realized_pnl", accounting.After.RealizedPnlAmount);
        AddDecimal(command, "@unrealized_pnl", accounting.UnrealizedPnlAmount);
        AddDecimal(command, "@fees", accounting.After.AccruedFeesAmount);
        AddDecimal(command, "@taxes", accounting.After.AccruedTaxesAmount);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@version", SqlDbType.Int).Value = accounting.After.Version;
        AddNullableDateTime(command, "@opened_at_utc", openedAt);
        AddDateTime(command, "@fill_at_utc", fill.FillAtUtc);
        AddNullableDateTime(command, "@closed_at_utc", closedAt);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }

    private async Task<long> InsertPositionEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionId,
        PositionAccountingResult accounting,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[position_events]
            (
                [position_event_uid], [position_id], [fill_id], [order_id],
                [event_sequence], [resulting_position_version], [event_type],
                [source_type], [side_before], [side_after], [quantity_before],
                [quantity_delta], [quantity_after], [average_price_before],
                [average_price_after], [cost_basis_before], [cost_basis_after],
                [realized_pnl_delta], [fees_delta], [taxes_delta],
                [event_at_utc], [source_service], [source_version],
                [correlation_id], [created_by]
            )
            OUTPUT INSERTED.[position_event_id]
            VALUES
            (
                NEWID(), @position_id, @fill_id, @order_id,
                @version, @version, @event_type,
                'FILL', @side_before, @side_after, @quantity_before,
                @quantity_delta, @quantity_after, @average_before,
                @average_after, @cost_before, @cost_after,
                @realized_delta, @fees_delta, @taxes_delta,
                @event_at_utc, @source_service, @source_version,
                @correlation_id, @actor
            );
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = fill.OrderId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = accounting.After.Version;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 40).Value = accounting.EventType;
        command.Parameters.Add("@side_before", SqlDbType.VarChar, 10).Value =
            PositionSide(accounting.Before.Direction);
        command.Parameters.Add("@side_after", SqlDbType.VarChar, 10).Value =
            PositionSide(accounting.After.Direction);
        AddDecimal(command, "@quantity_before", accounting.Before.Quantity);
        AddDecimal(command, "@quantity_delta", accounting.QuantityDelta);
        AddDecimal(command, "@quantity_after", accounting.After.Quantity);
        AddNullableDecimal(command, "@average_before", accounting.Before.AverageOpenPrice);
        AddNullableDecimal(command, "@average_after", accounting.After.AverageOpenPrice);
        AddDecimal(command, "@cost_before", accounting.Before.CostBasisAmount);
        AddDecimal(command, "@cost_after", accounting.After.CostBasisAmount);
        AddDecimal(command, "@realized_delta", accounting.NetRealizedPnlDelta);
        AddDecimal(command, "@fees_delta", accounting.FillFeesAmount);
        AddDecimal(command, "@taxes_delta", accounting.FillTaxesAmount);
        AddDateTime(command, "@event_at_utc", fill.FillAtUtc);
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            fill.CorrelationId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ApplyLotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionId,
        long positionEventId,
        PositionAccountingResult accounting,
        CancellationToken cancellationToken)
    {
        foreach (var lot in accounting.UpdatedExistingLots)
        {
            const string updateSql = """
                UPDATE [portfolio].[position_lots]
                SET [remaining_quantity] = @remaining,
                    [status] = @status,
                    [closed_at_utc] = @closed_at,
                    [updated_at_utc] = @fill_at,
                    [updated_by] = @actor
                WHERE [position_lot_id] = @lot_id;
                """;
            await using var command = Command(connection, transaction, updateSql);
            AddDecimal(command, "@remaining", lot.RemainingQuantity);
            command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
                lot.RemainingQuantity == 0m ? "CLOSED" : "OPEN";
            AddNullableDateTime(
                command,
                "@closed_at",
                lot.RemainingQuantity == 0m ? fill.FillAtUtc : null);
            AddDateTime(command, "@fill_at", fill.FillAtUtc);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            command.Parameters.Add("@lot_id", SqlDbType.BigInt).Value =
                lot.DatabaseId ?? throw new InvalidOperationException("Existing lot has no ID.");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var lot in accounting.NewLots)
        {
            const string insertSql = """
                INSERT INTO [portfolio].[position_lots]
                (
                    [position_lot_uid], [position_id], [opening_fill_id],
                    [lot_sequence], [lot_side], [opened_quantity], [remaining_quantity],
                    [open_price], [open_gross_amount], [allocated_open_fees_amount],
                    [allocated_open_taxes_amount], [opened_at_utc], [status],
                    [created_by], [updated_by]
                )
                VALUES
                (
                    @uid, @position_id, @fill_id,
                    @sequence, @side, @opened, @remaining,
                    @price, @gross, @fees,
                    @taxes, @opened_at, 'OPEN',
                    @actor, @actor
                );
                """;
            await using var command = Command(connection, transaction, insertSql);
            command.Parameters.Add("@uid", SqlDbType.UniqueIdentifier).Value = lot.LotUid;
            command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
            command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
            command.Parameters.Add("@sequence", SqlDbType.Int).Value = lot.Sequence;
            command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value =
                PositionSide(lot.Direction);
            AddDecimal(command, "@opened", lot.OpenedQuantity);
            AddDecimal(command, "@remaining", lot.RemainingQuantity);
            AddDecimal(command, "@price", lot.OpenPrice);
            AddDecimal(command, "@gross", lot.OpenedQuantity * lot.OpenPrice);
            AddDecimal(command, "@fees", lot.AllocatedOpenFeesAmount);
            AddDecimal(command, "@taxes", lot.AllocatedOpenTaxesAmount);
            AddDateTime(command, "@opened_at", lot.OpenedAtUtc);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var closure in accounting.Closures)
        {
            var closureId = await InsertClosureAsync(
                connection,
                transaction,
                fill,
                positionEventId,
                closure,
                cancellationToken);
            await InsertRealizedPnlAsync(
                connection,
                transaction,
                fill,
                positionId,
                closureId,
                closure,
                cancellationToken);
        }
    }

    private async Task<long> InsertClosureAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionEventId,
        PositionLotClosure closure,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[position_lot_closures]
            (
                [position_lot_closure_uid], [position_lot_id], [closing_fill_id],
                [position_event_id], [closure_sequence], [matched_quantity],
                [open_price], [close_price], [gross_realized_pnl_amount],
                [allocated_open_fees_amount], [allocated_close_fees_amount],
                [allocated_open_taxes_amount], [allocated_close_taxes_amount],
                [net_realized_pnl_amount], [matching_method], [closed_at_utc],
                [created_by]
            )
            OUTPUT INSERTED.[position_lot_closure_id]
            VALUES
            (
                NEWID(), @lot_id, @fill_id,
                @event_id, @sequence, @matched,
                @open_price, @close_price, @gross,
                @open_fees, @close_fees,
                @open_taxes, @close_taxes,
                @net, 'FIFO', @closed_at,
                @actor
            );
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@lot_id", SqlDbType.BigInt).Value =
            closure.Lot.DatabaseId ?? throw new InvalidOperationException("Closure lot has no ID.");
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@event_id", SqlDbType.BigInt).Value = positionEventId;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = closure.Sequence;
        AddDecimal(command, "@matched", closure.MatchedQuantity);
        AddDecimal(command, "@open_price", closure.OpenPrice);
        AddDecimal(command, "@close_price", closure.ClosePrice);
        AddDecimal(command, "@gross", closure.GrossRealizedPnlAmount);
        AddDecimal(command, "@open_fees", closure.AllocatedOpenFeesAmount);
        AddDecimal(command, "@close_fees", closure.AllocatedCloseFeesAmount);
        AddDecimal(command, "@open_taxes", closure.AllocatedOpenTaxesAmount);
        AddDecimal(command, "@close_taxes", closure.AllocatedCloseTaxesAmount);
        AddDecimal(command, "@net", closure.NetRealizedPnlAmount);
        AddDateTime(command, "@closed_at", fill.FillAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertRealizedPnlAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionId,
        long closureId,
        PositionLotClosure closure,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[realized_pnl_entries]
            (
                [realized_pnl_entry_uid], [portfolio_id], [position_id],
                [position_lot_closure_id], [closing_fill_id], [instrument_id],
                [trade_date], [currency_code], [gross_realized_pnl_amount],
                [fees_amount], [taxes_amount], [net_realized_pnl_amount],
                [recognized_at_utc], [correlation_id], [created_by]
            )
            VALUES
            (
                NEWID(), @portfolio_id, @position_id,
                @closure_id, @fill_id, @instrument_id,
                @trade_date, @currency, @gross,
                @fees, @taxes, @net,
                @recognized_at, @correlation_id, @actor
            );
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        command.Parameters.Add("@closure_id", SqlDbType.BigInt).Value = closureId;
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = fill.InstrumentId;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value = fill.FillAtUtc.UtcDateTime.Date;
        command.Parameters.Add("@currency", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        AddDecimal(command, "@gross", closure.GrossRealizedPnlAmount);
        AddDecimal(command, "@fees", closure.AllocatedOpenFeesAmount + closure.AllocatedCloseFeesAmount);
        AddDecimal(command, "@taxes", closure.AllocatedOpenTaxesAmount + closure.AllocatedCloseTaxesAmount);
        AddDecimal(command, "@net", closure.NetRealizedPnlAmount);
        AddDateTime(command, "@recognized_at", fill.FillAtUtc);
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = fill.CorrelationId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
