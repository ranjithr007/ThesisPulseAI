using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed class SqlServerPortfolioLedgerStore : IPortfolioLedgerStore
{
    private static readonly IReadOnlySet<string> AllowedTriggers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "UNKNOWN_OUTCOME", "STARTUP", "PERIODIC", "STREAM_RECOVERY",
            "QUANTITY_MISMATCH", "SESSION_SHUTDOWN", "OPERATOR_REQUEST",
        };

    private readonly SqlServerPortfolioLedgerOptions _options;

    public SqlServerPortfolioLedgerStore(SqlServerPortfolioLedgerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
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
                return Rejected(request, "FILL_OR_PORTFOLIO_NOT_FOUND");
            }

            if (await IsProjectedAsync(connection, transaction, fill.FillId, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return await DuplicateAsync(request, fill.InstrumentKey, cancellationToken);
            }

            var before = await ReadPositionAsync(connection, transaction, fill, cancellationToken);
            var result = DeterministicPositionAccounting.ApplyFill(
                before.State,
                before.Lots,
                new PositionFillInput(
                    fill.FillUid,
                    fill.Side,
                    fill.Quantity,
                    fill.Price,
                    fill.Fees,
                    fill.Taxes,
                    fill.FilledAtUtc));
            var positionId = await SavePositionAsync(
                connection,
                transaction,
                fill,
                before,
                result,
                cancellationToken);
            var eventId = await SavePositionEventAsync(
                connection,
                transaction,
                fill,
                positionId,
                result,
                cancellationToken);
            await SaveLotsAndPnlAsync(
                connection,
                transaction,
                fill,
                positionId,
                eventId,
                result,
                cancellationToken);
            await SaveCashAsync(connection, transaction, fill, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var snapshot = await GetSnapshotAsync(
                request.PortfolioCode,
                request.AsOfUtc,
                cancellationToken);
            return new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Projected,
                Array.Empty<string>(),
                snapshot?.Positions.FirstOrDefault(position =>
                    string.Equals(position.InstrumentKey, fill.InstrumentKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(position.ProductType, fill.ProductType, StringComparison.Ordinal)),
                snapshot,
                request.AsOfUtc);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await DuplicateAsync(request, null, cancellationToken);
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
        var portfolio = await ReadPortfolioAsync(connection, null, portfolioCode, false, cancellationToken);
        if (portfolio is null)
        {
            return null;
        }

        var positions = await ReadPositionsAsync(connection, portfolio, cancellationToken);
        var cash = await ReadCashAsync(connection, portfolio, cancellationToken);
        var gross = positions.Sum(position => position.MarketValueAmount);
        var net = positions.Sum(position => position.Direction switch
        {
            EvidenceDirectionV1.Long => position.MarketValueAmount,
            EvidenceDirectionV1.Short => -position.MarketValueAmount,
            _ => 0m,
        });
        return new PortfolioLedgerSnapshotV1(
            portfolio.Uid,
            portfolio.Code,
            portfolio.Environment,
            portfolio.AccountingMethod,
            portfolio.Currency,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        var trigger = request.TriggerType.Trim().ToUpperInvariant();
        if (!AllowedTriggers.Contains(trigger))
        {
            throw new ArgumentException("Unsupported reconciliation trigger.", nameof(request));
        }

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
            var runId = await SaveReconciliationRunAsync(
                connection,
                transaction,
                request,
                portfolio,
                trigger,
                runUid,
                discrepancies,
                cancellationToken);
            await SaveDiscrepanciesAsync(
                connection,
                transaction,
                runId,
                discrepancies,
                request.AsOfUtc,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LedgerReconciliationResultV1(
                request.RequestUid,
                runUid,
                request.PortfolioCode,
                discrepancies.Count == 0
                    ? PortfolioLedgerContractV1.Reconciled
                    : PortfolioLedgerContractV1.Discrepant,
                3,
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

    private async Task<FillRow?> ReadFillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT f.[fill_id], f.[fill_uid], f.[order_id], f.[instrument_id],
                   p.[portfolio_id], p.[portfolio_uid], p.[portfolio_code],
                   p.[environment], p.[accounting_method], p.[base_currency_code],
                   e.[exchange_code], i.[canonical_symbol],
                   CASE WHEN o.[position_intent] = 'INTRADAY' THEN 'INTRADAY'
                        WHEN o.[position_intent] = 'DELIVERY' THEN 'DELIVERY'
                        ELSE 'FUTURE' END,
                   f.[side], f.[fill_quantity], f.[fill_price],
                   COALESCE(f.[fees_amount],0), COALESCE(f.[taxes_amount],0),
                   f.[fill_at_utc], f.[correlation_id], f.[broker_account_id]
            FROM [execution].[fills] f WITH (UPDLOCK,HOLDLOCK)
            INNER JOIN [execution].[orders] o ON o.[order_id]=f.[order_id]
            INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id]=f.[trade_plan_id]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id]=tp.[signal_id]
            INNER JOIN [portfolio].[portfolios] p WITH (UPDLOCK,HOLDLOCK)
                ON p.[broker_account_id]=f.[broker_account_id]
               AND p.[environment]=f.[environment]
               AND p.[strategy_code]=s.[strategy_code]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id]=f.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id]=i.[exchange_id]
            WHERE f.[fill_uid]=@fill_uid AND p.[portfolio_code]=@portfolio_code
              AND p.[environment]='PAPER';
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = request.FillUid;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = request.PortfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FillRow(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetGuid(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9),
                $"{reader.GetString(10)}|{reader.GetString(11)}", reader.GetString(12),
                reader.GetString(13), reader.GetDecimal(14), reader.GetDecimal(15),
                reader.GetDecimal(16), reader.GetDecimal(17), Utc(reader, 18),
                reader.GetGuid(19), reader.GetInt64(20))
            : null;
    }

    private async Task<bool> IsProjectedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long fillId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP(1) 1 FROM [portfolio].[position_events] WITH (UPDLOCK,HOLDLOCK) WHERE [fill_id]=@fill_id;";
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
            SELECT TOP(1) [position_id],[position_uid],[position_side],[quantity],
                [average_open_price],[cost_basis_amount],[realized_pnl_amount],
                [accrued_fees_amount],[accrued_taxes_amount],[current_position_version],
                [opened_at_utc]
            FROM [portfolio].[positions] WITH (UPDLOCK,HOLDLOCK)
            WHERE [portfolio_id]=@portfolio_id AND [instrument_id]=@instrument_id
              AND [product_type]=@product_type;
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
                new PositionAccountingState(EvidenceDirectionV1.Neutral,0m,null,0m,0m,0m,0m,0),
                Array.Empty<PositionLotState>(),
                null);
        }

        var id = reader.GetInt64(0);
        var row = new PositionRow(
            id,
            reader.GetGuid(1),
            new PositionAccountingState(
                Direction(reader.GetString(2)), reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetInt32(9)),
            Array.Empty<PositionLotState>(),
            reader.IsDBNull(10) ? null : Utc(reader,10));
        await reader.DisposeAsync();
        return row with { Lots = await ReadLotsAsync(connection, transaction, id, cancellationToken) };
    }

    private async Task<IReadOnlyCollection<PositionLotState>> ReadLotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long positionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [position_lot_id],[position_lot_uid],[lot_sequence],[lot_side],
                [opened_quantity],[remaining_quantity],[open_price],
                [allocated_open_fees_amount],[allocated_open_taxes_amount],[opened_at_utc]
            FROM [portfolio].[position_lots] WITH (UPDLOCK,HOLDLOCK)
            WHERE [position_id]=@position_id ORDER BY [lot_sequence];
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PositionLotState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PositionLotState(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), Direction(reader.GetString(3)),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6),
                reader.GetDecimal(7), reader.GetDecimal(8), Utc(reader,9)));
        }
        return rows;
    }

    private async Task<long> SavePositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        PositionRow before,
        PositionAccountingResult result,
        CancellationToken cancellationToken)
    {
        var status = result.After.Quantity == 0 ? "CLOSED" : "OPEN";
        var side = Side(result.After.Direction);
        DateTimeOffset? openedAt = result.EventType == "REVERSED"
            ? fill.FilledAtUtc
            : before.OpenedAtUtc ?? (result.After.Quantity > 0 ? fill.FilledAtUtc : (DateTimeOffset?)null);
        DateTimeOffset? closedAt = result.After.Quantity == 0 ? fill.FilledAtUtc : null;

        if (before.Id is null)
        {
            const string sql = """
                INSERT INTO [portfolio].[positions]
                ([position_uid],[portfolio_id],[instrument_id],[product_type],[position_side],[quantity],
                 [average_open_price],[cost_basis_amount],[market_value_amount],[realized_pnl_amount],
                 [unrealized_pnl_amount],[accrued_fees_amount],[accrued_taxes_amount],
                 [protective_exit_quantity],[status],[current_position_version],[last_event_sequence],
                 [opened_at_utc],[last_fill_at_utc],[closed_at_utc],[last_valued_at_utc],
                 [created_by],[updated_by])
                OUTPUT INSERTED.[position_id]
                VALUES(@uid,@portfolio,@instrument,@product,@side,@qty,@avg,@cost,@market,@realized,
                       @unrealized,@fees,@taxes,0,@status,@version,@version,@opened,@fill_at,@closed,
                       @fill_at,@actor,@actor);
                """;
            await using var command = Command(connection, transaction, sql);
            AddPositionParameters(command, fill, before.Uid, result, side, status, openedAt, closedAt);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        const string update = """
            UPDATE [portfolio].[positions]
            SET [position_side]=@side,[quantity]=@qty,[average_open_price]=@avg,
                [cost_basis_amount]=@cost,[market_value_amount]=@market,
                [realized_pnl_amount]=@realized,[unrealized_pnl_amount]=@unrealized,
                [accrued_fees_amount]=@fees,[accrued_taxes_amount]=@taxes,[status]=@status,
                [current_position_version]=@version,[last_event_sequence]=@version,
                [opened_at_utc]=@opened,[last_fill_at_utc]=@fill_at,[closed_at_utc]=@closed,
                [last_valued_at_utc]=@fill_at,[updated_at_utc]=@fill_at,[updated_by]=@actor
            WHERE [position_id]=@id AND [current_position_version]=@expected;
            """;
        await using var updateCommand = Command(connection, transaction, update);
        AddPositionParameters(updateCommand, fill, before.Uid, result, side, status, openedAt, closedAt);
        updateCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = before.Id.Value;
        updateCommand.Parameters.Add("@expected", SqlDbType.Int).Value = before.State.Version;
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Position changed concurrently.");
        return before.Id.Value;
    }

    private void AddPositionParameters(
        SqlCommand command,
        FillRow fill,
        Guid uid,
        PositionAccountingResult result,
        string side,
        string status,
        DateTimeOffset? opened,
        DateTimeOffset? closed)
    {
        command.Parameters.Add("@uid",SqlDbType.UniqueIdentifier).Value=uid;
        command.Parameters.Add("@portfolio",SqlDbType.BigInt).Value=fill.PortfolioId;
        command.Parameters.Add("@instrument",SqlDbType.BigInt).Value=fill.InstrumentId;
        command.Parameters.Add("@product",SqlDbType.VarChar,30).Value=fill.ProductType;
        command.Parameters.Add("@side",SqlDbType.VarChar,10).Value=side;
        Decimal(command,"@qty",result.After.Quantity); NullableDecimal(command,"@avg",result.After.AverageOpenPrice);
        Decimal(command,"@cost",result.After.CostBasisAmount); Decimal(command,"@market",result.MarketValueAmount);
        Decimal(command,"@realized",result.After.RealizedPnlAmount); Decimal(command,"@unrealized",result.UnrealizedPnlAmount);
        Decimal(command,"@fees",result.After.AccruedFeesAmount); Decimal(command,"@taxes",result.After.AccruedTaxesAmount);
        command.Parameters.Add("@status",SqlDbType.VarChar,30).Value=status;
        command.Parameters.Add("@version",SqlDbType.Int).Value=result.After.Version;
        NullableTime(command,"@opened",opened); Time(command,"@fill_at",fill.FilledAtUtc); NullableTime(command,"@closed",closed);
        command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
    }

    private async Task<long> SavePositionEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionId,
        PositionAccountingResult result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[position_events]
            ([position_event_uid],[position_id],[fill_id],[order_id],[event_sequence],
             [resulting_position_version],[event_type],[source_type],[side_before],[side_after],
             [quantity_before],[quantity_delta],[quantity_after],[average_price_before],
             [average_price_after],[cost_basis_before],[cost_basis_after],[realized_pnl_delta],
             [fees_delta],[taxes_delta],[event_at_utc],[source_service],[source_version],
             [correlation_id],[created_by])
            OUTPUT INSERTED.[position_event_id]
            VALUES(NEWID(),@position,@fill,@order,@version,@version,@type,'FILL',@before_side,
                   @after_side,@before_qty,@delta,@after_qty,@before_avg,@after_avg,@before_cost,
                   @after_cost,@realized,@fees,@taxes,@event_at,@service,@source_version,@correlation,@actor);
            """;
        await using var command = Command(connection,transaction,sql);
        command.Parameters.Add("@position",SqlDbType.BigInt).Value=positionId;
        command.Parameters.Add("@fill",SqlDbType.BigInt).Value=fill.FillId;
        command.Parameters.Add("@order",SqlDbType.BigInt).Value=fill.OrderId;
        command.Parameters.Add("@version",SqlDbType.Int).Value=result.After.Version;
        command.Parameters.Add("@type",SqlDbType.VarChar,40).Value=result.EventType;
        command.Parameters.Add("@before_side",SqlDbType.VarChar,10).Value=Side(result.Before.Direction);
        command.Parameters.Add("@after_side",SqlDbType.VarChar,10).Value=Side(result.After.Direction);
        Decimal(command,"@before_qty",result.Before.Quantity); Decimal(command,"@delta",result.QuantityDelta);
        Decimal(command,"@after_qty",result.After.Quantity); NullableDecimal(command,"@before_avg",result.Before.AverageOpenPrice);
        NullableDecimal(command,"@after_avg",result.After.AverageOpenPrice); Decimal(command,"@before_cost",result.Before.CostBasisAmount);
        Decimal(command,"@after_cost",result.After.CostBasisAmount); Decimal(command,"@realized",result.NetRealizedPnlDelta);
        Decimal(command,"@fees",result.FillFeesAmount); Decimal(command,"@taxes",result.FillTaxesAmount);
        Time(command,"@event_at",fill.FilledAtUtc);
        command.Parameters.Add("@service",SqlDbType.VarChar,100).Value=_options.Actor;
        command.Parameters.Add("@source_version",SqlDbType.VarChar,50).Value=_options.SourceVersion;
        command.Parameters.Add("@correlation",SqlDbType.UniqueIdentifier).Value=fill.CorrelationId;
        command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task SaveLotsAndPnlAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillRow fill,
        long positionId,
        long eventId,
        PositionAccountingResult result,
        CancellationToken cancellationToken)
    {
        foreach (var lot in result.UpdatedExistingLots)
        {
            const string sql = """
                UPDATE [portfolio].[position_lots]
                SET [remaining_quantity]=@remaining,[status]=@status,[closed_at_utc]=@closed,
                    [updated_at_utc]=@fill_at,[updated_by]=@actor
                WHERE [position_lot_id]=@id;
                """;
            await using var command=Command(connection,transaction,sql);
            Decimal(command,"@remaining",lot.RemainingQuantity);
            command.Parameters.Add("@status",SqlDbType.VarChar,20).Value=lot.RemainingQuantity==0?"CLOSED":"OPEN";
            NullableTime(command,"@closed",lot.RemainingQuantity==0?fill.FilledAtUtc:(DateTimeOffset?)null);
            Time(command,"@fill_at",fill.FilledAtUtc); command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
            command.Parameters.Add("@id",SqlDbType.BigInt).Value=lot.DatabaseId ?? throw new InvalidOperationException("Lot ID missing.");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var lot in result.NewLots)
        {
            const string sql = """
                INSERT INTO [portfolio].[position_lots]
                ([position_lot_uid],[position_id],[opening_fill_id],[lot_sequence],[lot_side],
                 [opened_quantity],[remaining_quantity],[open_price],[open_gross_amount],
                 [allocated_open_fees_amount],[allocated_open_taxes_amount],[opened_at_utc],
                 [status],[created_by],[updated_by])
                VALUES(@uid,@position,@fill,@sequence,@side,@opened,@remaining,@price,@gross,
                       @fees,@taxes,@opened_at,'OPEN',@actor,@actor);
                """;
            await using var command=Command(connection,transaction,sql);
            command.Parameters.Add("@uid",SqlDbType.UniqueIdentifier).Value=lot.LotUid;
            command.Parameters.Add("@position",SqlDbType.BigInt).Value=positionId;
            command.Parameters.Add("@fill",SqlDbType.BigInt).Value=fill.FillId;
            command.Parameters.Add("@sequence",SqlDbType.Int).Value=lot.Sequence;
            command.Parameters.Add("@side",SqlDbType.VarChar,10).Value=Side(lot.Direction);
            Decimal(command,"@opened",lot.OpenedQuantity); Decimal(command,"@remaining",lot.RemainingQuantity);
            Decimal(command,"@price",lot.OpenPrice); Decimal(command,"@gross",lot.OpenedQuantity*lot.OpenPrice);
            Decimal(command,"@fees",lot.AllocatedOpenFeesAmount); Decimal(command,"@taxes",lot.AllocatedOpenTaxesAmount);
            Time(command,"@opened_at",lot.OpenedAtUtc); command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var closure in result.Closures)
        {
            const string closureSql = """
                INSERT INTO [portfolio].[position_lot_closures]
                ([position_lot_closure_uid],[position_lot_id],[closing_fill_id],[position_event_id],
                 [closure_sequence],[matched_quantity],[open_price],[close_price],
                 [gross_realized_pnl_amount],[allocated_open_fees_amount],
                 [allocated_close_fees_amount],[allocated_open_taxes_amount],
                 [allocated_close_taxes_amount],[net_realized_pnl_amount],[matching_method],
                 [closed_at_utc],[created_by])
                OUTPUT INSERTED.[position_lot_closure_id]
                VALUES(NEWID(),@lot,@fill,@event,@sequence,@qty,@open,@close,@gross,@open_fees,
                       @close_fees,@open_taxes,@close_taxes,@net,'FIFO',@closed,@actor);
                """;
            await using var command=Command(connection,transaction,closureSql);
            command.Parameters.Add("@lot",SqlDbType.BigInt).Value=closure.Lot.DatabaseId ?? throw new InvalidOperationException("Closure lot ID missing.");
            command.Parameters.Add("@fill",SqlDbType.BigInt).Value=fill.FillId;
            command.Parameters.Add("@event",SqlDbType.BigInt).Value=eventId;
            command.Parameters.Add("@sequence",SqlDbType.Int).Value=closure.Sequence;
            Decimal(command,"@qty",closure.MatchedQuantity); Decimal(command,"@open",closure.OpenPrice);
            Decimal(command,"@close",closure.ClosePrice); Decimal(command,"@gross",closure.GrossRealizedPnlAmount);
            Decimal(command,"@open_fees",closure.AllocatedOpenFeesAmount); Decimal(command,"@close_fees",closure.AllocatedCloseFeesAmount);
            Decimal(command,"@open_taxes",closure.AllocatedOpenTaxesAmount); Decimal(command,"@close_taxes",closure.AllocatedCloseTaxesAmount);
            Decimal(command,"@net",closure.NetRealizedPnlAmount); Time(command,"@closed",fill.FilledAtUtc);
            command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
            var closureId=Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),System.Globalization.CultureInfo.InvariantCulture);
            await SaveRealizedPnlAsync(connection,transaction,fill,positionId,closureId,closure,cancellationToken);
        }
    }

    private async Task SaveRealizedPnlAsync(
        SqlConnection connection, SqlTransaction transaction, FillRow fill, long positionId,
        long closureId, PositionLotClosure closure, CancellationToken cancellationToken)
    {
        const string sql="""
            INSERT INTO [portfolio].[realized_pnl_entries]
            ([realized_pnl_entry_uid],[portfolio_id],[position_id],[position_lot_closure_id],
             [closing_fill_id],[instrument_id],[trade_date],[currency_code],
             [gross_realized_pnl_amount],[fees_amount],[taxes_amount],[net_realized_pnl_amount],
             [recognized_at_utc],[correlation_id],[created_by])
            VALUES(NEWID(),@portfolio,@position,@closure,@fill,@instrument,@date,@currency,
                   @gross,@fees,@taxes,@net,@recognized,@correlation,@actor);
            """;
        await using var command=Command(connection,transaction,sql);
        command.Parameters.Add("@portfolio",SqlDbType.BigInt).Value=fill.PortfolioId;
        command.Parameters.Add("@position",SqlDbType.BigInt).Value=positionId;
        command.Parameters.Add("@closure",SqlDbType.BigInt).Value=closureId;
        command.Parameters.Add("@fill",SqlDbType.BigInt).Value=fill.FillId;
        command.Parameters.Add("@instrument",SqlDbType.BigInt).Value=fill.InstrumentId;
        command.Parameters.Add("@date",SqlDbType.Date).Value=fill.FilledAtUtc.UtcDateTime.Date;
        command.Parameters.Add("@currency",SqlDbType.Char,3).Value=fill.Currency;
        Decimal(command,"@gross",closure.GrossRealizedPnlAmount);
        Decimal(command,"@fees",closure.AllocatedOpenFeesAmount+closure.AllocatedCloseFeesAmount);
        Decimal(command,"@taxes",closure.AllocatedOpenTaxesAmount+closure.AllocatedCloseTaxesAmount);
        Decimal(command,"@net",closure.NetRealizedPnlAmount); Time(command,"@recognized",fill.FilledAtUtc);
        command.Parameters.Add("@correlation",SqlDbType.UniqueIdentifier).Value=fill.CorrelationId;
        command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveCashAsync(
        SqlConnection connection, SqlTransaction transaction, FillRow fill,
        CancellationToken cancellationToken)
    {
        var gross=fill.Quantity*fill.Price;
        var delta=fill.Side=="BUY"?-(gross+fill.Fees+fill.Taxes):gross-fill.Fees-fill.Taxes;
        if(delta==0) return;
        const string sql="""
            DECLARE @balance_id bigint,@settled decimal(19,6)=0,@receivable decimal(19,6)=0,
                    @payable decimal(19,6)=0,@reserved decimal(19,6)=0,@version int=0,@sequence bigint=0;
            SELECT @balance_id=[cash_balance_id],@settled=[settled_amount],
                   @receivable=[unsettled_receivable_amount],@payable=[unsettled_payable_amount],
                   @reserved=[reserved_amount],@version=[current_balance_version],
                   @sequence=[last_ledger_sequence]
            FROM [portfolio].[cash_balances] WITH(UPDLOCK,HOLDLOCK)
            WHERE [portfolio_id]=@portfolio AND [currency_code]=@currency;
            SET @settled=@settled+@delta; SET @version=@version+1; SET @sequence=@sequence+1;
            IF @balance_id IS NULL
            BEGIN
              INSERT INTO [portfolio].[cash_balances]
              ([portfolio_id],[currency_code],[settled_amount],[unsettled_receivable_amount],
               [unsettled_payable_amount],[reserved_amount],[total_balance_amount],[available_amount],
               [current_balance_version],[last_ledger_sequence],[as_of_utc],[created_by],[updated_by])
              VALUES(@portfolio,@currency,@settled,0,0,0,@settled,@settled,@version,@sequence,@at,@actor,@actor);
              SET @balance_id=SCOPE_IDENTITY();
            END
            ELSE
              UPDATE [portfolio].[cash_balances]
              SET [settled_amount]=@settled,
                  [total_balance_amount]=@settled+@receivable-@payable,
                  [available_amount]=@settled+@receivable-@payable-@reserved,
                  [current_balance_version]=@version,[last_ledger_sequence]=@sequence,
                  [as_of_utc]=@at,[updated_at_utc]=@at,[updated_by]=@actor
              WHERE [cash_balance_id]=@balance_id;
            INSERT INTO [portfolio].[cash_ledger_entries]
            ([cash_ledger_entry_uid],[portfolio_id],[cash_balance_id],[fill_id],[order_id],
             [ledger_sequence],[idempotency_key],[entry_type],[currency_code],
             [settled_delta_amount],[unsettled_receivable_delta_amount],
             [unsettled_payable_delta_amount],[reserved_delta_amount],[effective_at_utc],
             [posted_at_utc],[description],[correlation_id],[created_by])
            VALUES(NEWID(),@portfolio,@balance_id,@fill,@order,@sequence,@key,'TRADE_SETTLEMENT',
                   @currency,@delta,0,0,0,@at,@at,@description,@correlation,@actor);
            """;
        await using var command=Command(connection,transaction,sql);
        command.Parameters.Add("@portfolio",SqlDbType.BigInt).Value=fill.PortfolioId;
        command.Parameters.Add("@currency",SqlDbType.Char,3).Value=fill.Currency;
        Decimal(command,"@delta",delta); Time(command,"@at",fill.FilledAtUtc);
        command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
        command.Parameters.Add("@fill",SqlDbType.BigInt).Value=fill.FillId;
        command.Parameters.Add("@order",SqlDbType.BigInt).Value=fill.OrderId;
        command.Parameters.Add("@key",SqlDbType.VarChar,200).Value=$"FILL:{fill.FillUid:N}";
        command.Parameters.Add("@description",SqlDbType.NVarChar,1000).Value=$"PAPER {fill.Side} fill {fill.FillUid:D}";
        command.Parameters.Add("@correlation",SqlDbType.UniqueIdentifier).Value=fill.CorrelationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<PortfolioRow?> ReadPortfolioAsync(
        SqlConnection connection, SqlTransaction? transaction, string code, bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var hint=lockForUpdate?" WITH(UPDLOCK,HOLDLOCK)":string.Empty;
        var sql=$"SELECT [portfolio_id],[portfolio_uid],[portfolio_code],[environment],"+
                $"[broker_account_id],[strategy_code],[base_currency_code],[accounting_method] "+
                $"FROM [portfolio].[portfolios]{hint} WHERE [portfolio_code]=@code AND [environment]='PAPER';";
        await using var command=Command(connection,transaction,sql);
        command.Parameters.Add("@code",SqlDbType.VarChar,100).Value=code;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)?new PortfolioRow(
            reader.GetInt64(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),
            reader.GetInt64(4),reader.GetString(5),reader.GetString(6),reader.GetString(7)):null;
    }

    private async Task<IReadOnlyCollection<PositionLedgerSnapshotV1>> ReadPositionsAsync(
        SqlConnection connection, PortfolioRow portfolio, CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT p.[position_uid],e.[exchange_code],i.[canonical_symbol],p.[product_type],
                   p.[position_side],p.[quantity],p.[average_open_price],p.[cost_basis_amount],
                   p.[market_value_amount],p.[realized_pnl_amount],p.[unrealized_pnl_amount],
                   p.[accrued_fees_amount],p.[accrued_taxes_amount],p.[status],
                   p.[current_position_version],p.[opened_at_utc],p.[last_fill_at_utc],
                   p.[closed_at_utc],p.[updated_at_utc]
            FROM [portfolio].[positions] p
            INNER JOIN [reference].[instruments] i ON i.[instrument_id]=p.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id]=i.[exchange_id]
            WHERE p.[portfolio_id]=@id;
            """;
        await using var command=Command(connection,null,sql); command.Parameters.Add("@id",SqlDbType.BigInt).Value=portfolio.Id;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        var rows=new List<PositionLedgerSnapshotV1>();
        while(await reader.ReadAsync(cancellationToken)) rows.Add(new PositionLedgerSnapshotV1(
            reader.GetGuid(0),portfolio.Code,portfolio.Environment,$"{reader.GetString(1)}|{reader.GetString(2)}",
            reader.GetString(3),Direction(reader.GetString(4)),reader.GetDecimal(5),
            reader.IsDBNull(6)?null:reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),
            reader.GetDecimal(9),reader.GetDecimal(10),reader.GetDecimal(11),reader.GetDecimal(12),
            reader.GetString(13),reader.GetInt32(14),reader.IsDBNull(15)?null:Utc(reader,15),
            reader.IsDBNull(16)?null:Utc(reader,16),reader.IsDBNull(17)?null:Utc(reader,17),Utc(reader,18)));
        return rows;
    }

    private async Task<IReadOnlyCollection<CashLedgerSnapshotV1>> ReadCashAsync(
        SqlConnection connection, PortfolioRow portfolio, CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT [currency_code],[settled_amount],[unsettled_receivable_amount],
                   [unsettled_payable_amount],[reserved_amount],[total_balance_amount],
                   [available_amount],[current_balance_version],[last_ledger_sequence],[as_of_utc]
            FROM [portfolio].[cash_balances] WHERE [portfolio_id]=@id;
            """;
        await using var command=Command(connection,null,sql);command.Parameters.Add("@id",SqlDbType.BigInt).Value=portfolio.Id;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        var rows=new List<CashLedgerSnapshotV1>();
        while(await reader.ReadAsync(cancellationToken)) rows.Add(new CashLedgerSnapshotV1(
            portfolio.Code,reader.GetString(0),reader.GetDecimal(1),reader.GetDecimal(2),reader.GetDecimal(3),
            reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetInt32(7),reader.GetInt64(8),Utc(reader,9)));
        return rows;
    }

    private async Task<IReadOnlyCollection<LedgerDiscrepancyV1>> DetectDiscrepanciesAsync(
        SqlConnection connection, SqlTransaction transaction, PortfolioRow portfolio,
        CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT
              (SELECT COUNT(*) FROM [execution].[fills] f
               INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id]=f.[trade_plan_id]
               INNER JOIN [intelligence].[signals] s ON s.[signal_id]=tp.[signal_id]
               LEFT JOIN [portfolio].[position_events] pe ON pe.[fill_id]=f.[fill_id]
               WHERE f.[broker_account_id]=@account AND f.[environment]='PAPER'
                 AND s.[strategy_code]=@strategy AND pe.[position_event_id] IS NULL),
              (SELECT COUNT(*) FROM [portfolio].[positions] p
               OUTER APPLY(SELECT COALESCE(SUM(l.[remaining_quantity]),0) q
                           FROM [portfolio].[position_lots] l WHERE l.[position_id]=p.[position_id]) x
               WHERE p.[portfolio_id]=@portfolio AND p.[quantity]<>x.q),
              (SELECT COUNT(*) FROM [portfolio].[cash_balances] c
               WHERE c.[portfolio_id]=@portfolio AND
                    (c.[total_balance_amount]<>c.[settled_amount]+c.[unsettled_receivable_amount]-c.[unsettled_payable_amount]
                     OR c.[available_amount]<>c.[total_balance_amount]-c.[reserved_amount]));
            """;
        await using var command=Command(connection,transaction,sql);
        command.Parameters.Add("@account",SqlDbType.BigInt).Value=portfolio.BrokerAccountId;
        command.Parameters.Add("@strategy",SqlDbType.VarChar,100).Value=portfolio.Strategy;
        command.Parameters.Add("@portfolio",SqlDbType.BigInt).Value=portfolio.Id;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);await reader.ReadAsync(cancellationToken);
        var rows=new List<LedgerDiscrepancyV1>();
        AddCountDiscrepancy(rows,reader.GetInt32(0),"FILL_MISMATCH","Persisted fills are missing position projections.",portfolio.Code);
        AddCountDiscrepancy(rows,reader.GetInt32(1),"POSITION_MISMATCH","Position quantities differ from FIFO lots.",portfolio.Code);
        AddCountDiscrepancy(rows,reader.GetInt32(2),"FUNDS_MISMATCH","Cash balance identities are inconsistent.",portfolio.Code);
        return rows;
    }

    private static void AddCountDiscrepancy(
        ICollection<LedgerDiscrepancyV1> rows,int count,string type,string description,string scope)
    {
        if(count>0) rows.Add(new LedgerDiscrepancyV1(
            Guid.NewGuid(),type,type=="FUNDS_MISMATCH"?"CRITICAL":"HIGH",scope,
            $"{description} Count: {count}.",null,null,count,0m,true,true));
    }

    private async Task<long> SaveReconciliationRunAsync(
        SqlConnection connection, SqlTransaction transaction, LedgerReconciliationRequestV1 request,
        PortfolioRow portfolio,string trigger,Guid uid,IReadOnlyCollection<LedgerDiscrepancyV1> discrepancies,
        CancellationToken cancellationToken)
    {
        const string sql="""
            INSERT INTO [execution].[reconciliation_runs]
            ([reconciliation_run_uid],[broker_account_id],[environment],[trigger_type],
             [scope_type],[scope_reference],[status],[started_at_utc],[completed_at_utc],
             [observation_count],[discrepancy_count],[unresolved_material_count],
             [source_service],[source_version],[correlation_id],[created_by],[updated_by])
            OUTPUT INSERTED.[reconciliation_run_id]
            VALUES(@uid,@account,'PAPER',@trigger,'ACCOUNT',@scope,@status,@at,@at,3,@count,@count,
                   @service,@version,@correlation,@actor,@actor);
            """;
        await using var command=Command(connection,transaction,sql);
        command.Parameters.Add("@uid",SqlDbType.UniqueIdentifier).Value=uid;
        command.Parameters.Add("@account",SqlDbType.BigInt).Value=portfolio.BrokerAccountId;
        command.Parameters.Add("@trigger",SqlDbType.VarChar,40).Value=trigger;
        command.Parameters.Add("@scope",SqlDbType.VarChar,200).Value=portfolio.Code;
        command.Parameters.Add("@status",SqlDbType.VarChar,20).Value=discrepancies.Count==0?"SUCCEEDED":"PARTIAL";
        Time(command,"@at",request.AsOfUtc);command.Parameters.Add("@count",SqlDbType.Int).Value=discrepancies.Count;
        command.Parameters.Add("@service",SqlDbType.VarChar,100).Value=_options.Actor;
        command.Parameters.Add("@version",SqlDbType.VarChar,50).Value=_options.SourceVersion;
        command.Parameters.Add("@correlation",SqlDbType.UniqueIdentifier).Value=
            SqlServerMessageValues.ToDatabaseGuid(request.CorrelationId,nameof(request.CorrelationId));
        command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task SaveDiscrepanciesAsync(
        SqlConnection connection,SqlTransaction transaction,long runId,
        IReadOnlyCollection<LedgerDiscrepancyV1> rows,DateTimeOffset at,CancellationToken cancellationToken)
    {
        const string sql="""
            INSERT INTO [execution].[reconciliation_discrepancies]
            ([reconciliation_discrepancy_uid],[reconciliation_run_id],[discrepancy_type],
             [severity],[status],[description],[detected_at_utc],[blocks_new_exposure],
             [allows_risk_reducing_exits],[created_by],[updated_by])
            VALUES(@uid,@run,@type,@severity,'OPEN',@description,@at,1,1,@actor,@actor);
            """;
        foreach(var row in rows)
        {
            await using var command=Command(connection,transaction,sql);
            command.Parameters.Add("@uid",SqlDbType.UniqueIdentifier).Value=row.DiscrepancyUid;
            command.Parameters.Add("@run",SqlDbType.BigInt).Value=runId;
            command.Parameters.Add("@type",SqlDbType.VarChar,50).Value=row.Type;
            command.Parameters.Add("@severity",SqlDbType.VarChar,20).Value=row.Severity;
            command.Parameters.Add("@description",SqlDbType.NVarChar,2000).Value=row.Description;
            Time(command,"@at",at);command.Parameters.Add("@actor",SqlDbType.NVarChar,256).Value=_options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<PortfolioFillProjectionResultV1> DuplicateAsync(
        PortfolioFillProjectionRequestV1 request,string? instrumentKey,CancellationToken cancellationToken)
    {
        var snapshot=await GetSnapshotAsync(request.PortfolioCode,request.AsOfUtc,cancellationToken);
        return new PortfolioFillProjectionResultV1(
            request.RequestUid,request.FillUid,PortfolioLedgerContractV1.Duplicate,Array.Empty<string>(),
            snapshot?.Positions.FirstOrDefault(p=>instrumentKey is null||
                string.Equals(p.InstrumentKey,instrumentKey,StringComparison.OrdinalIgnoreCase)),snapshot,request.AsOfUtc);
    }

    private static PortfolioFillProjectionResultV1 Rejected(PortfolioFillProjectionRequestV1 request,string reason)=>
        new(request.RequestUid,request.FillUid,PortfolioLedgerContractV1.Rejected,[reason],null,null,request.AsOfUtc);

    private static void Validate(PortfolioFillProjectionRequestV1 request)
    {
        if(request.RequestUid==Guid.Empty||request.FillUid==Guid.Empty)
            throw new ArgumentException("Request and fill UIDs are required.",nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
    }

    private SqlCommand Command(SqlConnection connection,SqlTransaction? transaction,string sql)=>
        new(sql,connection,transaction){CommandTimeout=_options.CommandTimeoutSeconds};

    private static EvidenceDirectionV1 Direction(string value)=>value switch
    {
        "LONG"=>EvidenceDirectionV1.Long,"SHORT"=>EvidenceDirectionV1.Short,
        "FLAT"=>EvidenceDirectionV1.Neutral,_=>throw new InvalidOperationException($"Unknown side {value}.")
    };
    private static string Side(EvidenceDirectionV1 direction)=>direction switch
    {
        EvidenceDirectionV1.Long=>"LONG",EvidenceDirectionV1.Short=>"SHORT",
        EvidenceDirectionV1.Neutral=>"FLAT",_=>throw new ArgumentOutOfRangeException(nameof(direction))
    };
    private static void Decimal(SqlCommand command,string name,decimal value)
    {var p=command.Parameters.Add(name,SqlDbType.Decimal);p.Precision=19;p.Scale=6;p.Value=value;}
    private static void NullableDecimal(SqlCommand command,string name,decimal? value)
    {var p=command.Parameters.Add(name,SqlDbType.Decimal);p.Precision=19;p.Scale=6;p.Value=(object?)value??DBNull.Value;}
    private static void Time(SqlCommand command,string name,DateTimeOffset value)=>
        command.Parameters.Add(name,SqlDbType.DateTime2).Value=value.UtcDateTime;
    private static void NullableTime(SqlCommand command,string name,DateTimeOffset? value)=>
        command.Parameters.Add(name,SqlDbType.DateTime2).Value=value is null?DBNull.Value:value.Value.UtcDateTime;
    private static DateTimeOffset Utc(SqlDataReader reader,int ordinal)=>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal),DateTimeKind.Utc));

    private sealed record FillRow(
        long FillId,Guid FillUid,long OrderId,long InstrumentId,long PortfolioId,Guid PortfolioUid,
        string PortfolioCode,string Environment,string AccountingMethod,string Currency,
        string InstrumentKey,string ProductType,string Side,decimal Quantity,decimal Price,
        decimal Fees,decimal Taxes,DateTimeOffset FilledAtUtc,Guid CorrelationId,long BrokerAccountId);
    private sealed record PositionRow(
        long? Id,Guid Uid,PositionAccountingState State,IReadOnlyCollection<PositionLotState> Lots,
        DateTimeOffset? OpenedAtUtc);
    private sealed record PortfolioRow(
        long Id,Guid Uid,string Code,string Environment,long BrokerAccountId,string Strategy,
        string Currency,string AccountingMethod);
}
