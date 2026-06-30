using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed class SqlServerPortfolioLedgerStore : IPortfolioLedgerStore
{
    private static readonly IReadOnlySet<string> ReconciliationTriggers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "UNKNOWN_OUTCOME",
            "STARTUP",
            "PERIODIC",
            "STREAM_RECOVERY",
            "QUANTITY_MISMATCH",
            "SESSION_SHUTDOWN",
            "OPERATOR_REQUEST",
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
        ValidateProjectionRequest(request);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var fill = await LoadFillAsync(
                connection,
                transaction,
                request,
                cancellationToken);
            if (fill is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return RejectProjection(request, "FILL_OR_PORTFOLIO_NOT_FOUND");
            }

            if (await IsFillProjectedAsync(
                    connection,
                    transaction,
                    fill.FillId,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                var duplicateSnapshot = await GetSnapshotAsync(
                    request.PortfolioCode,
                    request.AsOfUtc,
                    cancellationToken);
                var duplicatePosition = duplicateSnapshot?.Positions.FirstOrDefault(
                    position => string.Equals(
                        position.InstrumentKey,
                        fill.InstrumentKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        position.ProductType,
                        fill.ProductType,
                        StringComparison.Ordinal));
                return new PortfolioFillProjectionResultV1(
                    request.RequestUid,
                    request.FillUid,
                    PortfolioLedgerContractV1.Duplicate,
                    Array.Empty<string>(),
                    duplicatePosition,
                    duplicateSnapshot,
                    request.AsOfUtc);
            }

            var position = await LoadPositionAsync(
                connection,
                transaction,
                fill,
                cancellationToken);
            var accounting = DeterministicPositionAccounting.ApplyFill(
                position.State,
                position.Lots,
                new PositionFillInput(
                    fill.FillUid,
                    fill.Side,
                    fill.Quantity,
                    fill.Price,
                    fill.FeesAmount,
                    fill.TaxesAmount,
                    fill.FillAtUtc));
            var positionId = await PersistPositionAsync(
                connection,
                transaction,
                fill,
                position,
                accounting,
                cancellationToken);
            var eventId = await InsertPositionEventAsync(
                connection,
                transaction,
                fill,
                positionId,
                accounting,
                cancellationToken);
            await PersistLotsAsync(
                connection,
                transaction,
                fill,
                positionId,
                eventId,
                accounting,
                cancellationToken);
            await PersistCashAsync(
                connection,
                transaction,
                fill,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            var portfolio = await GetSnapshotAsync(
                request.PortfolioCode,
                request.AsOfUtc,
                cancellationToken);
            var projectedPosition = portfolio?.Positions.FirstOrDefault(
                item => string.Equals(
                    item.InstrumentKey,
                    fill.InstrumentKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ProductType, fill.ProductType, StringComparison.Ordinal));
            return new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Projected,
                Array.Empty<string>(),
                projectedPosition,
                portfolio,
                request.AsOfUtc);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var duplicateSnapshot = await GetSnapshotAsync(
                request.PortfolioCode,
                request.AsOfUtc,
                cancellationToken);
            return new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Duplicate,
                Array.Empty<string>(),
                duplicateSnapshot?.Positions.FirstOrDefault(),
                duplicateSnapshot,
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
            lockForUpdate: false,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        var trigger = request.TriggerType.Trim().ToUpperInvariant();
        if (!ReconciliationTriggers.Contains(trigger))
        {
            throw new ArgumentException(
                "Unsupported reconciliation trigger type.",
                nameof(request));
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
                lockForUpdate: true,
                cancellationToken)
                ?? throw new InvalidOperationException("Portfolio was not found.");
            var discrepancies = new List<LedgerDiscrepancyV1>();
            await FindUnprojectedFillsAsync(
                connection,
                transaction,
                portfolio,
                discrepancies,
                cancellationToken);
            await FindPositionMismatchesAsync(
                connection,
                transaction,
                portfolio,
                discrepancies,
                cancellationToken);
            await FindCashMismatchesAsync(
                connection,
                transaction,
                portfolio,
                discrepancies,
                cancellationToken);

            var runUid = Guid.NewGuid();
            var runId = await InsertReconciliationRunAsync(
                connection,
                transaction,
                request,
                portfolio,
                trigger,
                runUid,
                discrepancies,
                cancellationToken);
            await InsertDiscrepanciesAsync(
                connection,
                transaction,
                runId,
                discrepancies,
                request.AsOfUtc,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var status = discrepancies.Count == 0
                ? PortfolioLedgerContractV1.Reconciled
                : PortfolioLedgerContractV1.Discrepant;
            return new LedgerReconciliationResultV1(
                request.RequestUid,
                runUid,
                request.PortfolioCode,
                status,
                discrepancies.Count + 1,
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

    private async Task<FillContext?> LoadFillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                f.[fill_id], f.[fill_uid], f.[order_id], f.[broker_account_id],
                f.[instrument_id], p.[portfolio_id], p.[portfolio_uid],
                p.[portfolio_code], p.[environment], p.[accounting_method],
                p.[base_currency_code], e.[exchange_code], i.[canonical_symbol],
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
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = request.FillUid;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
            request.PortfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FillContext(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            $"{reader.GetString(11)}|{reader.GetString(12)}",
            reader.GetString(13),
            reader.GetString(14),
            reader.GetDecimal(15),
            reader.GetDecimal(16),
            reader.GetDecimal(17),
            reader.GetDecimal(18),
            ReadUtc(reader, 19),
            reader.GetGuid(20));
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
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fillId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<PositionContext> LoadPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
        CancellationToken cancellationToken)
    {
        const string positionSql = """
            SELECT TOP (1)
                [position_id], [position_uid], [position_side], [quantity],
                [average_open_price], [cost_basis_amount], [realized_pnl_amount],
                [accrued_fees_amount], [accrued_taxes_amount],
                [current_position_version], [opened_at_utc], [last_fill_at_utc],
                [closed_at_utc], [market_value_amount], [unrealized_pnl_amount]
            FROM [portfolio].[positions] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [instrument_id] = @instrument_id
              AND [product_type] = @product_type;
            """;
        await using var positionCommand = CreateCommand(
            connection,
            transaction,
            positionSql);
        positionCommand.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value =
            fill.PortfolioId;
        positionCommand.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            fill.InstrumentId;
        positionCommand.Parameters.Add("@product_type", SqlDbType.VarChar, 30).Value =
            fill.ProductType;
        await using var reader = await positionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PositionContext(
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
                null,
                null,
                null,
                0m,
                0m);
        }

        var positionId = reader.GetInt64(0);
        var context = new PositionContext(
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
            reader.IsDBNull(10) ? null : ReadUtc(reader, 10),
            reader.IsDBNull(11) ? null : ReadUtc(reader, 11),
            reader.IsDBNull(12) ? null : ReadUtc(reader, 12),
            reader.GetDecimal(13),
            reader.GetDecimal(14));
        await reader.DisposeAsync();
        var lots = await LoadLotsAsync(
            connection,
            transaction,
            positionId,
            cancellationToken);
        return context with { Lots = lots };
    }

    private async Task<IReadOnlyCollection<PositionLotState>> LoadLotsAsync(
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
        await using var command = CreateCommand(connection, transaction, sql);
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

    private async Task<long> PersistPositionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
        PositionContext before,
        PositionAccountingResult accounting,
        CancellationToken cancellationToken)
    {
        var status = accounting.After.Quantity == 0 ? "CLOSED" : "OPEN";
        var side = ToSide(accounting.After.Direction);
        var openedAt = accounting.EventType == "REVERSED"
            ? fill.FillAtUtc
            : before.OpenedAtUtc ??
                (accounting.After.Quantity > 0 ? fill.FillAtUtc : null);
        var closedAt = accounting.After.Quantity == 0 ? fill.FillAtUtc : null;

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
            await using var command = CreateCommand(connection, transaction, insertSql);
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
        await using var updateCommand = CreateCommand(
            connection,
            transaction,
            updateSql);
        AddPositionParameters(
            updateCommand,
            fill,
            before.PositionUid,
            accounting,
            side,
            status,
            openedAt,
            closedAt);
        updateCommand.Parameters.Add("@position_id", SqlDbType.BigInt).Value =
            before.PositionId.Value;
        updateCommand.Parameters.Add("@expected_version", SqlDbType.Int).Value =
            before.State.Version;
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Position projection changed concurrently.");
        }

        return before.PositionId.Value;
    }

    private void AddPositionParameters(
        SqlCommand command,
        FillContext fill,
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
        AddDecimal(command, "@quantity", accounting.After.Quantity, 19, 6);
        AddNullableDecimal(command, "@average_price", accounting.After.AverageOpenPrice, 19, 6);
        AddDecimal(command, "@cost_basis", accounting.After.CostBasisAmount, 19, 6);
        AddDecimal(command, "@market_value", accounting.MarketValueAmount, 19, 6);
        AddDecimal(command, "@realized_pnl", accounting.After.RealizedPnlAmount, 19, 6);
        AddDecimal(command, "@unrealized_pnl", accounting.UnrealizedPnlAmount, 19, 6);
        AddDecimal(command, "@fees", accounting.After.AccruedFeesAmount, 19, 6);
        AddDecimal(command, "@taxes", accounting.After.AccruedTaxesAmount, 19, 6);
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
        FillContext fill,
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
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = fill.OrderId;
        command.Parameters.Add("@version", SqlDbType.Int).Value = accounting.After.Version;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 40).Value = accounting.EventType;
        command.Parameters.Add("@side_before", SqlDbType.VarChar, 10).Value =
            ToSide(accounting.Before.Direction);
        command.Parameters.Add("@side_after", SqlDbType.VarChar, 10).Value =
            ToSide(accounting.After.Direction);
        AddDecimal(command, "@quantity_before", accounting.Before.Quantity, 19, 6);
        AddDecimal(command, "@quantity_delta", accounting.QuantityDelta, 19, 6);
        AddDecimal(command, "@quantity_after", accounting.After.Quantity, 19, 6);
        AddNullableDecimal(command, "@average_before", accounting.Before.AverageOpenPrice, 19, 6);
        AddNullableDecimal(command, "@average_after", accounting.After.AverageOpenPrice, 19, 6);
        AddDecimal(command, "@cost_before", accounting.Before.CostBasisAmount, 19, 6);
        AddDecimal(command, "@cost_after", accounting.After.CostBasisAmount, 19, 6);
        AddDecimal(command, "@realized_delta", accounting.NetRealizedPnlDelta, 19, 6);
        AddDecimal(command, "@fees_delta", accounting.FillFeesAmount, 19, 6);
        AddDecimal(command, "@taxes_delta", accounting.FillTaxesAmount, 19, 6);
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

    private async Task PersistLotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
        long positionId,
        long positionEventId,
        PositionAccountingResult accounting,
        CancellationToken cancellationToken)
    {
        foreach (var lot in accounting.UpdatedExistingLots)
        {
            const string updateSql = """
                UPDATE [portfolio].[position_lots]
                SET [remaining_quantity] = @remaining_quantity,
                    [status] = @status,
                    [closed_at_utc] = @closed_at_utc,
                    [updated_at_utc] = @fill_at_utc,
                    [updated_by] = @actor
                WHERE [position_lot_id] = @lot_id;
                """;
            await using var command = CreateCommand(connection, transaction, updateSql);
            AddDecimal(command, "@remaining_quantity", lot.RemainingQuantity, 19, 6);
            command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
                lot.RemainingQuantity == 0 ? "CLOSED" : "OPEN";
            AddNullableDateTime(
                command,
                "@closed_at_utc",
                lot.RemainingQuantity == 0 ? fill.FillAtUtc : null);
            AddDateTime(command, "@fill_at_utc", fill.FillAtUtc);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            command.Parameters.Add("@lot_id", SqlDbType.BigInt).Value =
                lot.DatabaseId ?? throw new InvalidOperationException("Existing lot has no database ID.");
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
                    @lot_uid, @position_id, @fill_id,
                    @sequence, @side, @opened_quantity, @remaining_quantity,
                    @open_price, @gross, @fees,
                    @taxes, @opened_at_utc, 'OPEN',
                    @actor, @actor
                );
                """;
            await using var command = CreateCommand(connection, transaction, insertSql);
            command.Parameters.Add("@lot_uid", SqlDbType.UniqueIdentifier).Value = lot.LotUid;
            command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
            command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
            command.Parameters.Add("@sequence", SqlDbType.Int).Value = lot.Sequence;
            command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = ToSide(lot.Direction);
            AddDecimal(command, "@opened_quantity", lot.OpenedQuantity, 19, 6);
            AddDecimal(command, "@remaining_quantity", lot.RemainingQuantity, 19, 6);
            AddDecimal(command, "@open_price", lot.OpenPrice, 19, 6);
            AddDecimal(command, "@gross", lot.OpenedQuantity * lot.OpenPrice, 19, 6);
            AddDecimal(command, "@fees", lot.AllocatedOpenFeesAmount, 19, 6);
            AddDecimal(command, "@taxes", lot.AllocatedOpenTaxesAmount, 19, 6);
            AddDateTime(command, "@opened_at_utc", lot.OpenedAtUtc);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var closure in accounting.Closures)
        {
            var closureId = await InsertLotClosureAsync(
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

    private async Task<long> InsertLotClosureAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
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
                @position_event_id, @sequence, @matched_quantity,
                @open_price, @close_price, @gross_pnl,
                @open_fees, @close_fees,
                @open_taxes, @close_taxes,
                @net_pnl, 'FIFO', @closed_at_utc,
                @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@lot_id", SqlDbType.BigInt).Value =
            closure.Lot.DatabaseId ?? throw new InvalidOperationException("Closure lot has no database ID.");
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@position_event_id", SqlDbType.BigInt).Value = positionEventId;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = closure.Sequence;
        AddDecimal(command, "@matched_quantity", closure.MatchedQuantity, 19, 6);
        AddDecimal(command, "@open_price", closure.OpenPrice, 19, 6);
        AddDecimal(command, "@close_price", closure.ClosePrice, 19, 6);
        AddDecimal(command, "@gross_pnl", closure.GrossRealizedPnlAmount, 19, 6);
        AddDecimal(command, "@open_fees", closure.AllocatedOpenFeesAmount, 19, 6);
        AddDecimal(command, "@close_fees", closure.AllocatedCloseFeesAmount, 19, 6);
        AddDecimal(command, "@open_taxes", closure.AllocatedOpenTaxesAmount, 19, 6);
        AddDecimal(command, "@close_taxes", closure.AllocatedCloseTaxesAmount, 19, 6);
        AddDecimal(command, "@net_pnl", closure.NetRealizedPnlAmount, 19, 6);
        AddDateTime(command, "@closed_at_utc", fill.FillAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertRealizedPnlAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
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
                @trade_date, @currency_code, @gross_pnl,
                @fees, @taxes, @net_pnl,
                @recognized_at_utc, @correlation_id, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = positionId;
        command.Parameters.Add("@closure_id", SqlDbType.BigInt).Value = closureId;
        command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = fill.InstrumentId;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value = fill.FillAtUtc.UtcDateTime.Date;
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        AddDecimal(command, "@gross_pnl", closure.GrossRealizedPnlAmount, 19, 6);
        AddDecimal(
            command,
            "@fees",
            closure.AllocatedOpenFeesAmount + closure.AllocatedCloseFeesAmount,
            19,
            6);
        AddDecimal(
            command,
            "@taxes",
            closure.AllocatedOpenTaxesAmount + closure.AllocatedCloseTaxesAmount,
            19,
            6);
        AddDecimal(command, "@net_pnl", closure.NetRealizedPnlAmount, 19, 6);
        AddDateTime(command, "@recognized_at_utc", fill.FillAtUtc);
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            fill.CorrelationId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PersistCashAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        FillContext fill,
        CancellationToken cancellationToken)
    {
        var gross = fill.Quantity * fill.Price;
        var cashDelta = fill.Side == "BUY"
            ? -(gross + fill.FeesAmount + fill.TaxesAmount)
            : gross - fill.FeesAmount - fill.TaxesAmount;
        if (cashDelta == 0)
        {
            return;
        }

        const string readSql = """
            SELECT TOP (1)
                [cash_balance_id], [settled_amount], [unsettled_receivable_amount],
                [unsettled_payable_amount], [reserved_amount],
                [current_balance_version], [last_ledger_sequence]
            FROM [portfolio].[cash_balances] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [currency_code] = @currency_code;
            """;
        await using var readCommand = CreateCommand(connection, transaction, readSql);
        readCommand.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        readCommand.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
        long? balanceId = null;
        decimal settled = 0m;
        decimal receivable = 0m;
        decimal payable = 0m;
        decimal reserved = 0m;
        var version = 0;
        long sequence = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            balanceId = reader.GetInt64(0);
            settled = reader.GetDecimal(1);
            receivable = reader.GetDecimal(2);
            payable = reader.GetDecimal(3);
            reserved = reader.GetDecimal(4);
            version = reader.GetInt32(5);
            sequence = reader.GetInt64(6);
        }

        await reader.DisposeAsync();
        settled += cashDelta;
        version++;
        sequence++;
        var total = settled + receivable - payable;
        var available = total - reserved;

        if (balanceId is null)
        {
            const string insertBalanceSql = """
                INSERT INTO [portfolio].[cash_balances]
                (
                    [portfolio_id], [currency_code], [settled_amount],
                    [unsettled_receivable_amount], [unsettled_payable_amount],
                    [reserved_amount], [total_balance_amount], [available_amount],
                    [current_balance_version], [last_ledger_sequence], [as_of_utc],
                    [created_by], [updated_by]
                )
                OUTPUT INSERTED.[cash_balance_id]
                VALUES
                (
                    @portfolio_id, @currency_code, @settled,
                    0, 0,
                    0, @total, @available,
                    @version, @sequence, @as_of_utc,
                    @actor, @actor
                );
                """;
            await using var insertCommand = CreateCommand(
                connection,
                transaction,
                insertBalanceSql);
            AddCashParameters(
                insertCommand,
                fill,
                settled,
                total,
                available,
                version,
                sequence);
            balanceId = Convert.ToInt64(
                await insertCommand.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            const string updateBalanceSql = """
                UPDATE [portfolio].[cash_balances]
                SET [settled_amount] = @settled,
                    [total_balance_amount] = @total,
                    [available_amount] = @available,
                    [current_balance_version] = @version,
                    [last_ledger_sequence] = @sequence,
                    [as_of_utc] = @as_of_utc,
                    [updated_at_utc] = @as_of_utc,
                    [updated_by] = @actor
                WHERE [cash_balance_id] = @balance_id
                  AND [current_balance_version] = @expected_version;
                """;
            await using var updateCommand = CreateCommand(
                connection,
                transaction,
                updateBalanceSql);
            AddCashParameters(
                updateCommand,
                fill,
                settled,
                total,
                available,
                version,
                sequence);
            updateCommand.Parameters.Add("@balance_id", SqlDbType.BigInt).Value =
                balanceId.Value;
            updateCommand.Parameters.Add("@expected_version", SqlDbType.Int).Value = version - 1;
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Cash projection changed concurrently.");
            }
        }

        const string ledgerSql = """
            INSERT INTO [portfolio].[cash_ledger_entries]
            (
                [cash_ledger_entry_uid], [portfolio_id], [cash_balance_id],
                [fill_id], [order_id], [ledger_sequence], [idempotency_key],
                [entry_type], [currency_code], [settled_delta_amount],
                [unsettled_receivable_delta_amount], [unsettled_payable_delta_amount],
                [reserved_delta_amount], [effective_at_utc], [posted_at_utc],
                [description], [correlation_id], [created_by]
            )
            VALUES
            (
                NEWID(), @portfolio_id, @balance_id,
                @fill_id, @order_id, @sequence, @idempotency_key,
                'TRADE_SETTLEMENT', @currency_code, @cash_delta,
                0, 0,
                0, @effective_at_utc, @effective_at_utc,
                @description, @correlation_id, @actor
            );
            """;
        await using var ledgerCommand = CreateCommand(connection, transaction, ledgerSql);
        ledgerCommand.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        ledgerCommand.Parameters.Add("@balance_id", SqlDbType.BigInt).Value = balanceId.Value;
        ledgerCommand.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = fill.FillId;
        ledgerCommand.Parameters.Add("@order_id", SqlDbType.BigInt).Value = fill.OrderId;
        ledgerCommand.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        ledgerCommand.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            $"FILL:{fill.FillUid:N}";
        ledgerCommand.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value =
            fill.CurrencyCode;
        AddDecimal(ledgerCommand, "@cash_delta", cashDelta, 19, 6);
        AddDateTime(ledgerCommand, "@effective_at_utc", fill.FillAtUtc);
        ledgerCommand.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value =
            $"PAPER {fill.Side} fill {fill.FillUid:D}";
        ledgerCommand.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            fill.CorrelationId;
        ledgerCommand.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddCashParameters(
        SqlCommand command,
        FillContext fill,
        decimal settled,
        decimal total,
        decimal available,
        int version,
        long sequence)
    {
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = fill.PortfolioId;
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = fill.CurrencyCode;
        AddDecimal(command, "@settled", settled, 19, 6);
        AddDecimal(command, "@total", total, 19, 6);
        AddDecimal(command, "@available", available, 19, 6);
        command.Parameters.Add("@version", SqlDbType.Int).Value = version;
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        AddDateTime(command, "@as_of_utc", fill.FillAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }

    private async Task<PortfolioContext?> ReadPortfolioAsync(
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
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
            portfolioCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PortfolioContext(
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
        PortfolioContext portfolio,
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
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var positions = new List<PositionLedgerSnapshotV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            positions.Add(new PositionLedgerSnapshotV1(
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

        return positions;
    }

    private async Task<IReadOnlyCollection<CashLedgerSnapshotV1>> ReadCashSnapshotsAsync(
        SqlConnection connection,
        PortfolioContext portfolio,
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
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var balances = new List<CashLedgerSnapshotV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            balances.Add(new CashLedgerSnapshotV1(
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

        return balances;
    }

    private async Task FindUnprojectedFillsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioContext portfolio,
        ICollection<LedgerDiscrepancyV1> discrepancies,
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
            WHERE f.[broker_account_id] = @broker_account_id
              AND f.[environment] = 'PAPER'
              AND s.[strategy_code] = @strategy_code
              AND pe.[position_event_id] IS NULL;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value =
            portfolio.BrokerAccountId;
        command.Parameters.Add("@strategy_code", SqlDbType.VarChar, 100).Value =
            portfolio.StrategyCode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            discrepancies.Add(new LedgerDiscrepancyV1(
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

    private async Task FindPositionMismatchesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioContext portfolio,
        ICollection<LedgerDiscrepancyV1> discrepancies,
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
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            discrepancies.Add(new LedgerDiscrepancyV1(
                Guid.NewGuid(),
                "POSITION_MISMATCH",
                "HIGH",
                reader.GetGuid(0).ToString("D"),
                $"Position quantity for {reader.GetString(1)}|{reader.GetString(2)} differs from FIFO lots.",
                reader.GetDecimal(4),
                reader.GetDecimal(3),
                null,
                null,
                true,
                true));
        }
    }

    private async Task FindCashMismatchesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioContext portfolio,
        ICollection<LedgerDiscrepancyV1> discrepancies,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [currency_code], [total_balance_amount],
                [settled_amount] + [unsettled_receivable_amount] - [unsettled_payable_amount],
                [available_amount], [total_balance_amount] - [reserved_amount]
            FROM [portfolio].[cash_balances]
            WHERE [portfolio_id] = @portfolio_id
              AND
              (
                  [total_balance_amount] <>
                      [settled_amount] + [unsettled_receivable_amount] - [unsettled_payable_amount]
                  OR [available_amount] <> [total_balance_amount] - [reserved_amount]
              );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolio.PortfolioId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            discrepancies.Add(new LedgerDiscrepancyV1(
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

    private async Task<long> InsertReconciliationRunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        LedgerReconciliationRequestV1 request,
        PortfolioContext portfolio,
        string trigger,
        Guid runUid,
        IReadOnlyCollection<LedgerDiscrepancyV1> discrepancies,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[reconciliation_runs]
            (
                [reconciliation_run_uid], [broker_account_id], [environment],
                [trigger_type], [scope_type], [scope_reference], [status],
                [started_at_utc], [completed_at_utc], [observation_count],
                [discrepancy_count], [unresolved_material_count],
                [source_service], [source_version], [correlation_id],
                [created_by], [updated_by]
            )
            OUTPUT INSERTED.[reconciliation_run_id]
            VALUES
            (
                @run_uid, @broker_account_id, 'PAPER',
                @trigger_type, 'ACCOUNT', @scope_reference, @status,
                @started_at_utc, @started_at_utc, @observation_count,
                @discrepancy_count, @discrepancy_count,
                @source_service, @source_version, @correlation_id,
                @actor, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@run_uid", SqlDbType.UniqueIdentifier).Value = runUid;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value =
            portfolio.BrokerAccountId;
        command.Parameters.Add("@trigger_type", SqlDbType.VarChar, 40).Value = trigger;
        command.Parameters.Add("@scope_reference", SqlDbType.VarChar, 200).Value =
            request.PortfolioCode;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
            discrepancies.Count == 0 ? "SUCCEEDED" : "PARTIAL";
        AddDateTime(command, "@started_at_utc", request.AsOfUtc);
        command.Parameters.Add("@observation_count", SqlDbType.Int).Value =
            discrepancies.Count + 1;
        command.Parameters.Add("@discrepancy_count", SqlDbType.Int).Value =
            discrepancies.Count;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                request.CorrelationId,
                nameof(request.CorrelationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertDiscrepanciesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long runId,
        IReadOnlyCollection<LedgerDiscrepancyV1> discrepancies,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[reconciliation_discrepancies]
            (
                [reconciliation_discrepancy_uid], [reconciliation_run_id],
                [discrepancy_type], [severity], [status], [description],
                [detected_at_utc], [blocks_new_exposure],
                [allows_risk_reducing_exits], [created_by], [updated_by]
            )
            VALUES
            (
                @discrepancy_uid, @run_id,
                @type, @severity, 'OPEN', @description,
                @detected_at_utc, @blocks_new_exposure,
                @allows_exits, @actor, @actor
            );
            """;
        foreach (var discrepancy in discrepancies)
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@discrepancy_uid", SqlDbType.UniqueIdentifier).Value =
                discrepancy.DiscrepancyUid;
            command.Parameters.Add("@run_id", SqlDbType.BigInt).Value = runId;
            command.Parameters.Add("@type", SqlDbType.VarChar, 50).Value = discrepancy.Type;
            command.Parameters.Add("@severity", SqlDbType.VarChar, 20).Value =
                discrepancy.Severity;
            command.Parameters.Add("@description", SqlDbType.NVarChar, 2000).Value =
                discrepancy.Description;
            AddDateTime(command, "@detected_at_utc", detectedAtUtc);
            command.Parameters.Add("@blocks_new_exposure", SqlDbType.Bit).Value =
                discrepancy.BlocksNewExposure;
            command.Parameters.Add("@allows_exits", SqlDbType.Bit).Value =
                discrepancy.AllowsRiskReducingExits;
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static PortfolioFillProjectionResultV1 RejectProjection(
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

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static EvidenceDirectionV1 ParseDirection(string value) => value switch
    {
        "LONG" => EvidenceDirectionV1.Long,
        "SHORT" => EvidenceDirectionV1.Short,
        "FLAT" => EvidenceDirectionV1.Neutral,
        _ => throw new InvalidOperationException($"Unknown position side '{value}'."),
    };

    private static string ToSide(EvidenceDirectionV1 direction) => direction switch
    {
        EvidenceDirectionV1.Long => "LONG",
        EvidenceDirectionV1.Short => "SHORT",
        EvidenceDirectionV1.Neutral => "FLAT",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static void AddDateTime(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value.UtcDateTime;

    private static void AddNullableDateTime(
        SqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value =
            value is null ? DBNull.Value : value.Value.UtcDateTime;

    private static void AddDecimal(
        SqlCommand command,
        string name,
        decimal value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string name,
        decimal? value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record FillContext(
        long FillId,
        Guid FillUid,
        long OrderId,
        long BrokerAccountId,
        long InstrumentId,
        long PortfolioId,
        Guid PortfolioUid,
        string PortfolioCode,
        string Environment,
        string AccountingMethod,
        string CurrencyCode,
        string InstrumentKey,
        string ProductType,
        string Side,
        decimal Quantity,
        decimal Price,
        decimal FeesAmount,
        decimal TaxesAmount,
        DateTimeOffset FillAtUtc,
        Guid CorrelationId);

    private sealed record PositionContext(
        long? PositionId,
        Guid PositionUid,
        PositionAccountingState State,
        IReadOnlyCollection<PositionLotState> Lots,
        DateTimeOffset? OpenedAtUtc,
        DateTimeOffset? LastFillAtUtc,
        DateTimeOffset? ClosedAtUtc,
        decimal MarketValueAmount,
        decimal UnrealizedPnlAmount);

    private sealed record PortfolioContext(
        long PortfolioId,
        Guid PortfolioUid,
        string PortfolioCode,
        string Environment,
        long BrokerAccountId,
        string StrategyCode,
        string CurrencyCode,
        string AccountingMethod);
}
