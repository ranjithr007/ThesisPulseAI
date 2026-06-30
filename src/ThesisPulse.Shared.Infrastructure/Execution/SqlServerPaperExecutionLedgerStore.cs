using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Execution;

public sealed class SqlServerPaperExecutionLedgerStore : IPaperExecutionLedgerStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerPaperExecutionLedgerOptions _options;

    public SqlServerPaperExecutionLedgerStore(
        SqlServerPaperExecutionLedgerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<ExecutionCommandResultV1?> FindAuthorizationAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        const string sql = """
            SELECT TOP (1) ec.[raw_contract_json]
            FROM [execution].[execution_commands] ec
            INNER JOIN [broker].[broker_accounts] ba
                ON ba.[broker_account_id] = ec.[broker_account_id]
            WHERE ec.[environment] = 'PAPER'
              AND ba.[account_reference] = @account_reference
              AND ec.[idempotency_key] = @idempotency_key
              AND ec.[command_type] = 'PLACE';
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@account_reference", SqlDbType.VarChar, 100).Value =
            _options.BrokerAccountReference;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            idempotencyKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : JsonSerializer.Deserialize<ExecutionCommandResultV1>(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                JsonOptions)
                ?? throw new InvalidOperationException(
                    "Stored execution command payload could not be deserialized.");
    }

    public async Task<ExecutionCommandResultV1> PersistAuthorizationAsync(
        ExecutionCommandResultV1 result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var commandContract = result.Command ?? throw new ArgumentException(
            "Authorized result must contain an execution command.",
            nameof(result));
        var orderContract = result.PaperOrder ?? throw new ArgumentException(
            "Authorized result must contain a paper order.",
            nameof(result));

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existing = await FindAuthorizationAsync(
                connection,
                transaction,
                commandContract.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (existing.Command?.TradePlanUid == commandContract.TradePlanUid &&
                    string.Equals(
                        existing.Command.CorrelationId,
                        commandContract.CorrelationId,
                        StringComparison.Ordinal))
                {
                    return existing;
                }

                throw new PaperExecutionIdempotencyConflictException(
                    "The idempotency key is already bound to another trade plan.");
            }

            var lineage = await ResolveLineageAsync(
                connection,
                transaction,
                commandContract,
                cancellationToken);
            var rawJson = JsonSerializer.Serialize(result, JsonOptions);
            var commandId = await InsertCommandAsync(
                connection,
                transaction,
                result,
                lineage,
                rawJson,
                cancellationToken);
            await InsertCommandEventAndStateAsync(
                connection,
                transaction,
                commandContract,
                commandId,
                cancellationToken);
            var orderId = await InsertOrderAsync(
                connection,
                transaction,
                commandContract,
                orderContract,
                lineage,
                commandId,
                cancellationToken);
            await InsertOrderEventAsync(
                connection,
                transaction,
                orderContract,
                lineage,
                commandId,
                orderId,
                eventUid: orderContract.PaperOrderUid,
                previousState: null,
                eventType: PaperOrderStateContractV1.Created,
                rawJson: JsonSerializer.Serialize(orderContract, JsonOptions),
                occurredAtUtc: orderContract.CreatedAtUtc,
                cancellationToken);
            await UpdateCommandResultOrderAsync(
                connection,
                transaction,
                commandId,
                orderContract.PaperOrderUid,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await FindAuthorizationAsync(
                commandContract.IdempotencyKey,
                cancellationToken);
            if (existing is not null &&
                existing.Command?.TradePlanUid == commandContract.TradePlanUid)
            {
                return existing;
            }

            throw new PaperExecutionIdempotencyConflictException(
                "Execution command or order uniqueness was violated.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PaperOrderSnapshotV1?> GetOrderAsync(
        Guid paperOrderUid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadOrderAsync(
            connection,
            null,
            paperOrderUid,
            lockForUpdate: false,
            cancellationToken);
    }

    public async Task<PaperOrderTransitionResultV1> ApplyEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        Func<PaperOrderSnapshotV1, PaperOrderTransitionResultV1> transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transition);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var replay = await FindEventReplayAsync(
                connection,
                transaction,
                request.EventUid,
                paperOrderUid,
                cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var row = await ReadOrderRowAsync(
                connection,
                transaction,
                paperOrderUid,
                cancellationToken);
            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PaperOrderTransitionResultV1(
                    false,
                    false,
                    ["PAPER_ORDER_NOT_FOUND"],
                    null,
                    request.OccurredAtUtc);
            }

            var result = transition(row.Snapshot);
            if (!result.Applied || result.PaperOrder is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return result;
            }

            var updated = result.PaperOrder;
            var rows = await UpdateOrderAsync(
                connection,
                transaction,
                row,
                updated,
                cancellationToken);
            if (rows != 1)
            {
                throw new PaperExecutionConcurrencyException(
                    "The paper order changed while applying the event.");
            }

            var rawJson = JsonSerializer.Serialize(request, JsonOptions);
            await InsertOrderEventAsync(
                connection,
                transaction,
                updated,
                row.Lineage,
                row.ExecutionCommandId,
                row.OrderId,
                request.EventUid,
                row.Snapshot.State,
                updated.State,
                rawJson,
                request.OccurredAtUtc,
                cancellationToken);

            Guid? fillUid = null;
            if (string.Equals(
                    request.EventType,
                    PaperOrderEventContractV1.Fill,
                    StringComparison.OrdinalIgnoreCase))
            {
                fillUid = request.EventUid;
                await InsertFillAsync(
                    connection,
                    transaction,
                    row,
                    request,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return result with { FillUid = fillUid };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ExecutionCommandResultV1?> FindAuthorizationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) ec.[raw_contract_json]
            FROM [execution].[execution_commands] ec WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [broker].[broker_accounts] ba
                ON ba.[broker_account_id] = ec.[broker_account_id]
            WHERE ec.[environment] = 'PAPER'
              AND ba.[account_reference] = @account_reference
              AND ec.[idempotency_key] = @idempotency_key
              AND ec.[command_type] = 'PLACE';
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@account_reference", SqlDbType.VarChar, 100).Value =
            _options.BrokerAccountReference;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            idempotencyKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : JsonSerializer.Deserialize<ExecutionCommandResultV1>((string)value, JsonOptions);
    }

    private async Task<ExecutionLineage> ResolveLineageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ExecutionCommandV1 commandContract,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                tp.[trade_plan_id], tp.[risk_decision_id], tp.[thesis_id],
                tp.[signal_id], tp.[instrument_id], ba.[broker_account_id],
                rd.[risk_decision_uid], th.[thesis_uid], s.[signal_uid],
                e.[exchange_code], i.[canonical_symbol]
            FROM [risk].[trade_plans] tp WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [risk].[risk_decisions] rd
                ON rd.[risk_decision_id] = tp.[risk_decision_id]
            INNER JOIN [thesis].[theses] th
                ON th.[thesis_id] = tp.[thesis_id]
            INNER JOIN [intelligence].[signals] s
                ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [reference].[instruments] i
                ON i.[instrument_id] = tp.[instrument_id]
            INNER JOIN [reference].[exchanges] e
                ON e.[exchange_id] = i.[exchange_id]
            CROSS JOIN [broker].[broker_accounts] ba
            WHERE tp.[trade_plan_uid] = @trade_plan_uid
              AND tp.[environment] = 'PAPER'
              AND tp.[is_current] = 1
              AND ba.[environment] = 'PAPER'
              AND ba.[account_reference] = @account_reference
              AND ba.[status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY')
              AND ba.[allows_risk_reducing_exits] = 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@trade_plan_uid", SqlDbType.UniqueIdentifier).Value =
            commandContract.TradePlanUid;
        command.Parameters.Add("@account_reference", SqlDbType.VarChar, 100).Value =
            _options.BrokerAccountReference;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Canonical PAPER trade-plan or broker-account lineage was not found.");
        }

        var lineage = new ExecutionLineage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetGuid(6),
            reader.GetGuid(7),
            reader.GetGuid(8),
            $"{reader.GetString(9)}|{reader.GetString(10)}");
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Execution lineage resolution was ambiguous.");
        }

        if (lineage.RiskDecisionUid != commandContract.RiskDecisionUid ||
            lineage.ThesisUid != commandContract.ThesisUid ||
            lineage.SignalUid != commandContract.SignalUid ||
            !string.Equals(
                lineage.InstrumentKey,
                commandContract.InstrumentKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Execution command lineage does not match the canonical trade plan.");
        }

        return lineage;
    }

    private async Task<long> InsertCommandAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ExecutionCommandResultV1 result,
        ExecutionLineage lineage,
        string rawJson,
        CancellationToken cancellationToken)
    {
        var contract = result.Command!;
        const string sql = """
            INSERT INTO [execution].[execution_commands]
            (
                [execution_command_uid], [message_uid], [trade_plan_id],
                [broker_account_id], [instrument_id], [contract_version],
                [environment], [source_service], [source_version], [command_type],
                [idempotency_key], [execution_policy_version], [side],
                [position_intent], [quantity], [order_type], [limit_price],
                [trigger_price], [time_in_force], [client_order_id],
                [requested_at_utc], [generated_at_utc], [valid_until_utc],
                [correlation_id], [raw_contract_json], [contract_hash], [created_by]
            )
            OUTPUT INSERTED.[execution_command_id]
            VALUES
            (
                @command_uid, @message_uid, @trade_plan_id,
                @broker_account_id, @instrument_id, '1.0.0',
                'PAPER', @source_service, @source_version, 'PLACE',
                @idempotency_key, @policy_version, @side,
                @position_intent, @quantity, @order_type, @limit_price,
                @trigger_price, @time_in_force, @client_order_id,
                @requested_at_utc, @generated_at_utc, @valid_until_utc,
                @correlation_id, @raw_json, @contract_hash, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value =
            contract.ExecutionCommandUid;
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            contract.RequestUid;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = lineage.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = lineage.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = lineage.InstrumentId;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value = contract.IdempotencyKey;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 50).Value = contract.ExecutionPolicyVersion;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = contract.Side;
        command.Parameters.Add("@position_intent", SqlDbType.VarChar, 20).Value = contract.PositionIntent;
        AddDecimal(command, "@quantity", contract.Quantity, 19, 6);
        command.Parameters.Add("@order_type", SqlDbType.VarChar, 20).Value = contract.Entry.OrderType;
        AddNullableDecimal(command, "@limit_price", contract.Entry.LimitPrice, 19, 6);
        AddNullableDecimal(command, "@trigger_price", contract.Entry.TriggerPrice, 19, 6);
        command.Parameters.Add("@time_in_force", SqlDbType.VarChar, 10).Value = contract.TimeInForce;
        command.Parameters.Add("@client_order_id", SqlDbType.VarChar, 100).Value =
            result.PaperOrder!.PaperOrderUid.ToString("N");
        AddDateTime(command, "@requested_at_utc", contract.AuthorizedAtUtc);
        AddDateTime(command, "@generated_at_utc", contract.AuthorizedAtUtc);
        AddDateTime(command, "@valid_until_utc", contract.ValidUntilUtc);
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(contract.CorrelationId, nameof(contract.CorrelationId));
        command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@contract_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertCommandEventAndStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ExecutionCommandV1 contract,
        long commandId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[execution_command_events]
            (
                [execution_command_event_uid], [execution_command_id], [event_sequence],
                [event_type], [command_status], [outcome_classification],
                [occurred_at_utc], [source_service], [source_version],
                [correlation_id], [created_by]
            )
            VALUES
            (
                NEWID(), @command_id, 0, 'PERSISTED', 'PERSISTED', 'NONE',
                @occurred_at_utc, @source_service, @source_version,
                @correlation_id, @actor
            );

            INSERT INTO [execution].[execution_command_states]
            (
                [execution_command_id], [current_status], [outcome_classification],
                [last_event_sequence], [can_retry_without_reconciliation],
                [broker_contacted], [reconciliation_required], [updated_by]
            )
            VALUES (@command_id, 'PERSISTED', 'NONE', 0, 0, 0, 0, @actor);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = commandId;
        AddDateTime(command, "@occurred_at_utc", contract.AuthorizedAtUtc);
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(contract.CorrelationId, nameof(contract.CorrelationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> InsertOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ExecutionCommandV1 contract,
        PaperOrderSnapshotV1 order,
        ExecutionLineage lineage,
        long commandId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[orders]
            (
                [order_uid], [place_execution_command_id], [trade_plan_id],
                [broker_account_id], [instrument_id], [environment], [side],
                [position_intent], [requested_quantity], [filled_quantity],
                [remaining_quantity], [average_fill_price], [order_type],
                [limit_price], [trigger_price], [time_in_force], [client_order_id],
                [current_status], [current_order_version], [last_accepted_event_at_utc],
                [is_terminal], [reconciliation_required], [created_by], [updated_by]
            )
            OUTPUT INSERTED.[order_id]
            VALUES
            (
                @order_uid, @command_id, @trade_plan_id,
                @broker_account_id, @instrument_id, 'PAPER', @side,
                @position_intent, @requested_quantity, 0,
                @remaining_quantity, NULL, @order_type,
                @limit_price, @trigger_price, @time_in_force, @client_order_id,
                'CREATED', @version, @event_at_utc,
                0, 0, @actor, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = order.PaperOrderUid;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = lineage.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = lineage.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = lineage.InstrumentId;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = order.Side;
        command.Parameters.Add("@position_intent", SqlDbType.VarChar, 20).Value = contract.PositionIntent;
        AddDecimal(command, "@requested_quantity", order.RequestedQuantity, 19, 6);
        AddDecimal(command, "@remaining_quantity", order.RemainingQuantity, 19, 6);
        command.Parameters.Add("@order_type", SqlDbType.VarChar, 20).Value = contract.Entry.OrderType;
        AddNullableDecimal(command, "@limit_price", contract.Entry.LimitPrice, 19, 6);
        AddNullableDecimal(command, "@trigger_price", contract.Entry.TriggerPrice, 19, 6);
        command.Parameters.Add("@time_in_force", SqlDbType.VarChar, 10).Value = contract.TimeInForce;
        command.Parameters.Add("@client_order_id", SqlDbType.VarChar, 100).Value = order.PaperOrderUid.ToString("N");
        command.Parameters.Add("@version", SqlDbType.Int).Value = order.Version;
        AddDateTime(command, "@event_at_utc", order.CreatedAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task UpdateCommandResultOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long commandId,
        Guid orderUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [execution].[execution_command_states]
            SET [result_order_uid] = @order_uid,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [execution_command_id] = @command_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<PaperOrderTransitionResultV1?> FindEventReplayAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid eventUid,
        Guid orderUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) f.[fill_uid]
            FROM [execution].[order_events] oe WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [execution].[orders] o ON o.[order_id] = oe.[order_id]
            LEFT JOIN [execution].[fills] f ON f.[fill_uid] = oe.[order_event_uid]
            WHERE oe.[order_event_uid] = @event_uid AND o.[order_uid] = @order_uid;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value = eventUid;
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            return null;
        }

        var snapshot = await ReadOrderAsync(
            connection,
            transaction,
            orderUid,
            lockForUpdate: true,
            cancellationToken);
        return new PaperOrderTransitionResultV1(
            true,
            true,
            Array.Empty<string>(),
            snapshot,
            snapshot?.UpdatedAtUtc ?? DateTimeOffset.UtcNow,
            value is DBNull ? null : (Guid?)value);
    }

    private async Task<OrderRow?> ReadOrderRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid orderUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                o.[order_id], o.[place_execution_command_id], o.[trade_plan_id],
                o.[broker_account_id], o.[instrument_id], o.[order_uid],
                ec.[execution_command_uid], tp.[risk_decision_id], tp.[thesis_id],
                tp.[signal_id], rd.[risk_decision_uid], th.[thesis_uid], s.[signal_uid],
                tp.[correlation_id], ec.[idempotency_key], o.[environment],
                e.[exchange_code], i.[canonical_symbol], o.[side], o.[current_status],
                o.[requested_quantity], o.[filled_quantity], o.[remaining_quantity],
                o.[average_fill_price], tp.[allow_partial_fill], o.[current_order_version],
                o.[created_at_utc], o.[updated_at_utc],
                CASE WHEN o.[is_terminal] = 1 THEN o.[updated_at_utc] ELSE NULL END,
                NULL
            FROM [execution].[orders] o WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id] = o.[trade_plan_id]
            INNER JOIN [risk].[risk_decisions] rd ON rd.[risk_decision_id] = tp.[risk_decision_id]
            INNER JOIN [thesis].[theses] th ON th.[thesis_id] = tp.[thesis_id]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = o.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            WHERE o.[order_uid] = @order_uid;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lineage = new ExecutionLineage(
            reader.GetInt64(2),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(4),
            reader.GetInt64(3),
            reader.GetGuid(10),
            reader.GetGuid(11),
            reader.GetGuid(12),
            $"{reader.GetString(16)}|{reader.GetString(17)}");
        var side = reader.GetString(18);
        var snapshot = new PaperOrderSnapshotV1(
            reader.GetGuid(5),
            reader.GetGuid(6),
            commandTradePlanUid: await ReadTradePlanUidAsync(connection, transaction, reader.GetInt64(2), cancellationToken),
            lineage.RiskDecisionUid,
            lineage.ThesisUid,
            lineage.SignalUid,
            reader.GetGuid(13).ToString("D"),
            reader.GetString(14),
            reader.GetString(15),
            lineage.InstrumentKey,
            side == "BUY" ? EvidenceDirectionV1.Long : EvidenceDirectionV1.Short,
            side,
            reader.GetString(19),
            reader.GetDecimal(20),
            reader.GetDecimal(21),
            reader.GetDecimal(22),
            reader.IsDBNull(23) ? null : reader.GetDecimal(23),
            reader.GetBoolean(24),
            reader.GetInt32(25),
            ReadUtc(reader, 26),
            ReadUtc(reader, 27),
            reader.IsDBNull(28) ? null : ReadUtc(reader, 28),
            reader.IsDBNull(29) ? null : reader.GetString(29));
        return new OrderRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            lineage,
            snapshot);
    }

    private async Task<PaperOrderSnapshotV1?> ReadOrderAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid orderUid,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        if (transaction is not null && lockForUpdate)
        {
            return (await ReadOrderRowAsync(connection, transaction, orderUid, cancellationToken))?.Snapshot;
        }

        const string sql = """
            SELECT
                o.[order_uid], ec.[execution_command_uid], tp.[trade_plan_uid],
                rd.[risk_decision_uid], th.[thesis_uid], s.[signal_uid],
                tp.[correlation_id], ec.[idempotency_key], o.[environment],
                e.[exchange_code], i.[canonical_symbol], o.[side], o.[current_status],
                o.[requested_quantity], o.[filled_quantity], o.[remaining_quantity],
                o.[average_fill_price], tp.[allow_partial_fill], o.[current_order_version],
                o.[created_at_utc], o.[updated_at_utc],
                CASE WHEN o.[is_terminal] = 1 THEN o.[updated_at_utc] ELSE NULL END
            FROM [execution].[orders] o
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [risk].[trade_plans] tp ON tp.[trade_plan_id] = o.[trade_plan_id]
            INNER JOIN [risk].[risk_decisions] rd ON rd.[risk_decision_id] = tp.[risk_decision_id]
            INNER JOIN [thesis].[theses] th ON th.[thesis_id] = tp.[thesis_id]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = o.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            WHERE o.[order_uid] = @order_uid;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var side = reader.GetString(11);
        return new PaperOrderSnapshotV1(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetGuid(6).ToString("D"),
            reader.GetString(7),
            reader.GetString(8),
            $"{reader.GetString(9)}|{reader.GetString(10)}",
            side == "BUY" ? EvidenceDirectionV1.Long : EvidenceDirectionV1.Short,
            side,
            reader.GetString(12),
            reader.GetDecimal(13),
            reader.GetDecimal(14),
            reader.GetDecimal(15),
            reader.IsDBNull(16) ? null : reader.GetDecimal(16),
            reader.GetBoolean(17),
            reader.GetInt32(18),
            ReadUtc(reader, 19),
            ReadUtc(reader, 20),
            reader.IsDBNull(21) ? null : ReadUtc(reader, 21),
            null);
    }

    private async Task<Guid> ReadTradePlanUidAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long tradePlanId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT [trade_plan_uid] FROM [risk].[trade_plans] WHERE [trade_plan_id] = @id;";
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = tradePlanId;
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Trade plan disappeared during order read."));
    }

    private async Task<int> UpdateOrderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OrderRow row,
        PaperOrderSnapshotV1 updated,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [execution].[orders]
            SET [filled_quantity] = @filled_quantity,
                [remaining_quantity] = @remaining_quantity,
                [average_fill_price] = @average_fill_price,
                [current_status] = @status,
                [current_order_version] = @new_version,
                [last_accepted_event_at_utc] = @event_at_utc,
                [is_terminal] = @is_terminal,
                [updated_at_utc] = @event_at_utc,
                [updated_by] = @actor
            WHERE [order_id] = @order_id
              AND [current_order_version] = @expected_version;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        AddDecimal(command, "@filled_quantity", updated.FilledQuantity, 19, 6);
        AddDecimal(command, "@remaining_quantity", updated.RemainingQuantity, 19, 6);
        AddNullableDecimal(command, "@average_fill_price", updated.AverageFillPrice, 19, 6);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = updated.State;
        command.Parameters.Add("@new_version", SqlDbType.Int).Value = updated.Version;
        AddDateTime(command, "@event_at_utc", updated.UpdatedAtUtc);
        command.Parameters.Add("@is_terminal", SqlDbType.Bit).Value =
            updated.TerminalAtUtc is not null;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = row.OrderId;
        command.Parameters.Add("@expected_version", SqlDbType.Int).Value = row.Snapshot.Version;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOrderEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PaperOrderSnapshotV1 order,
        ExecutionLineage lineage,
        long commandId,
        long orderId,
        Guid eventUid,
        string? previousState,
        string eventType,
        string rawJson,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[order_events]
            (
                [order_event_uid], [message_uid], [order_id], [execution_command_id],
                [trade_plan_id], [broker_account_id], [instrument_id], [contract_version],
                [environment], [source_service], [source_version], [side],
                [event_type], [previous_status], [normalized_status],
                [requested_quantity], [filled_quantity], [remaining_quantity],
                [average_fill_price], [event_at_utc], [received_at_utc],
                [generated_at_utc], [order_version], [is_reconciliation_event],
                [projection_disposition], [correlation_id], [raw_contract_json],
                [contract_hash], [created_by]
            )
            VALUES
            (
                @event_uid, @event_uid, @order_id, @command_id,
                @trade_plan_id, @broker_account_id, @instrument_id, '1.0.0',
                'PAPER', @source_service, @source_version, @side,
                @event_type, @previous_status, @normalized_status,
                @requested_quantity, @filled_quantity, @remaining_quantity,
                @average_fill_price, @event_at_utc, @event_at_utc,
                @event_at_utc, @order_version, 0,
                'ACCEPTED', @correlation_id, @raw_json,
                @contract_hash, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value = eventUid;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = orderId;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = commandId;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = lineage.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = lineage.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = lineage.InstrumentId;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = order.Side;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 30).Value = eventType;
        command.Parameters.Add("@previous_status", SqlDbType.VarChar, 30).Value =
            (object?)previousState ?? DBNull.Value;
        command.Parameters.Add("@normalized_status", SqlDbType.VarChar, 30).Value = order.State;
        AddDecimal(command, "@requested_quantity", order.RequestedQuantity, 19, 6);
        AddDecimal(command, "@filled_quantity", order.FilledQuantity, 19, 6);
        AddDecimal(command, "@remaining_quantity", order.RemainingQuantity, 19, 6);
        AddNullableDecimal(command, "@average_fill_price", order.AverageFillPrice, 19, 6);
        AddDateTime(command, "@event_at_utc", occurredAtUtc);
        command.Parameters.Add("@order_version", SqlDbType.Int).Value = order.Version;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(order.CorrelationId, nameof(order.CorrelationId));
        command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@contract_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertFillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OrderRow row,
        PaperOrderEventRequestV1 request,
        CancellationToken cancellationToken)
    {
        var rawJson = JsonSerializer.Serialize(request, JsonOptions);
        var quantity = request.FillQuantity
            ?? throw new InvalidOperationException("Applied fill has no quantity.");
        var price = request.FillPrice
            ?? throw new InvalidOperationException("Applied fill has no price.");
        var gross = quantity * price;
        const string sql = """
            INSERT INTO [execution].[fills]
            (
                [fill_uid], [message_uid], [order_id], [execution_command_id],
                [trade_plan_id], [broker_account_id], [instrument_id], [contract_version],
                [environment], [source_service], [source_version], [fill_fingerprint],
                [side], [fill_quantity], [fill_price], [gross_amount], [fees_amount],
                [taxes_amount], [net_amount], [currency_code], [liquidity_role],
                [fill_at_utc], [received_at_utc], [generated_at_utc],
                [is_reconciliation_fill], [correlation_id], [raw_contract_json],
                [contract_hash], [created_by]
            )
            VALUES
            (
                @fill_uid, @fill_uid, @order_id, @command_id,
                @trade_plan_id, @broker_account_id, @instrument_id, '1.0.0',
                'PAPER', @source_service, @source_version, @fingerprint,
                @side, @quantity, @price, @gross, 0,
                0, @gross, @currency, 'UNKNOWN',
                @fill_at_utc, @fill_at_utc, @fill_at_utc,
                0, @correlation_id, @raw_json,
                @contract_hash, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = request.EventUid;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = row.OrderId;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = row.ExecutionCommandId;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = row.Lineage.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = row.Lineage.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = row.Lineage.InstrumentId;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@fingerprint", SqlDbType.VarChar, 256).Value =
            $"PAPER:{row.Snapshot.PaperOrderUid:N}:{request.EventUid:N}";
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = row.Snapshot.Side;
        AddDecimal(command, "@quantity", quantity, 19, 6);
        AddDecimal(command, "@price", price, 19, 6);
        AddDecimal(command, "@gross", gross, 19, 6);
        command.Parameters.Add("@currency", SqlDbType.Char, 3).Value =
            _options.CurrencyCode.Trim().ToUpperInvariant();
        AddDateTime(command, "@fill_at_utc", request.OccurredAtUtc);
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                row.Snapshot.CorrelationId,
                nameof(row.Snapshot.CorrelationId));
        command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@contract_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static void AddDateTime(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value.UtcDateTime;

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

    private sealed record ExecutionLineage(
        long TradePlanId,
        long RiskDecisionId,
        long ThesisId,
        long SignalId,
        long InstrumentId,
        long BrokerAccountId,
        Guid RiskDecisionUid,
        Guid ThesisUid,
        Guid SignalUid,
        string InstrumentKey);

    private sealed record OrderRow(
        long OrderId,
        long ExecutionCommandId,
        ExecutionLineage Lineage,
        PaperOrderSnapshotV1 Snapshot);
}
