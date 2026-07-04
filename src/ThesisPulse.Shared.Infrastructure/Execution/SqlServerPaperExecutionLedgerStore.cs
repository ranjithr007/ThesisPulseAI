using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Execution;

/// <summary>
/// SQL Server execution ledger that accepts the authoritative signal-risk and candidate-thesis
/// lineage used by automatic PAPER execution while retaining legacy risk_decisions/theses support.
/// </summary>
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
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await FindAuthorizationCoreAsync(
            connection,
            null,
            idempotencyKey,
            lockForUpdate: false,
            cancellationToken);
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
            "Authorized result must contain a PAPER order.",
            nameof(result));

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existing = await FindAuthorizationCoreAsync(
                connection,
                transaction,
                commandContract.IdempotencyKey,
                lockForUpdate: true,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ValidateReplay(existing, commandContract);
            }

            var instrument = ParseInstrumentKey(commandContract.InstrumentKey);
            var rawResultJson = JsonSerializer.Serialize(result, JsonOptions);
            var rawOrderJson = JsonSerializer.Serialize(orderContract, JsonOptions);
            const string sql = """
                DECLARE @trade_plan_id bigint;
                DECLARE @broker_account_id bigint;
                DECLARE @instrument_id bigint;
                DECLARE @execution_command_id bigint;
                DECLARE @order_id bigint;

                SELECT
                    @trade_plan_id = tp.[trade_plan_id],
                    @instrument_id = tp.[instrument_id]
                FROM [risk].[trade_plans] tp WITH (UPDLOCK, HOLDLOCK)
                LEFT JOIN [risk].[risk_decisions] rd
                    ON rd.[risk_decision_id] = tp.[risk_decision_id]
                LEFT JOIN [risk].[signal_risk_evaluations] sre
                    ON sre.[signal_risk_evaluation_id] = tp.[signal_risk_evaluation_id]
                LEFT JOIN [thesis].[theses] th
                    ON th.[thesis_id] = tp.[thesis_id]
                LEFT JOIN [intelligence].[signal_fusion_lineage] candidate_lineage
                    ON candidate_lineage.[signal_id] = tp.[signal_id]
                   AND candidate_lineage.[thesis_uid] = tp.[candidate_thesis_uid]
                INNER JOIN [intelligence].[signals] s
                    ON s.[signal_id] = tp.[signal_id]
                INNER JOIN [reference].[instruments] i
                    ON i.[instrument_id] = tp.[instrument_id]
                INNER JOIN [reference].[exchanges] e
                    ON e.[exchange_id] = i.[exchange_id]
                WHERE tp.[trade_plan_uid] = @trade_plan_uid
                  AND
                  (
                      (tp.[risk_decision_id] IS NOT NULL
                          AND rd.[risk_decision_uid] = @risk_decision_uid)
                      OR
                      (tp.[signal_risk_evaluation_id] IS NOT NULL
                          AND sre.[risk_decision_uid] = @risk_decision_uid
                          AND sre.[current_status] = 'RISK_APPROVED')
                  )
                  AND
                  (
                      (tp.[thesis_id] IS NOT NULL
                          AND th.[thesis_uid] = @thesis_uid)
                      OR
                      (tp.[candidate_thesis_uid] IS NOT NULL
                          AND candidate_lineage.[thesis_uid] = @thesis_uid)
                  )
                  AND s.[signal_uid] = @signal_uid
                  AND tp.[correlation_id] = @correlation_id
                  AND s.[correlation_id] = @correlation_id
                  AND tp.[initial_status] = 'READY'
                  AND tp.[environment] = 'PAPER'
                  AND tp.[is_current] = 1
                  AND tp.[valid_until_utc] > @authorized_at_utc
                  AND e.[exchange_code] = @exchange_code
                  AND i.[canonical_symbol] = @instrument_value;

                SELECT @broker_account_id = ba.[broker_account_id]
                FROM [broker].[broker_accounts] ba WITH (UPDLOCK, HOLDLOCK)
                WHERE ba.[environment] = 'PAPER'
                  AND ba.[account_reference] = @account_reference
                  AND ba.[status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY')
                  AND ba.[allows_risk_reducing_exits] = 1;

                IF @trade_plan_id IS NULL OR @instrument_id IS NULL
                    THROW 57101, 'Canonical PAPER PLAN_READY lineage was not found.', 1;

                IF @broker_account_id IS NULL
                    THROW 57102, 'Configured PAPER broker account was not found.', 1;

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
                VALUES
                (
                    @command_uid, @message_uid, @trade_plan_id,
                    @broker_account_id, @instrument_id, '1.0.0',
                    'PAPER', @source_service, @source_version, 'PLACE',
                    @idempotency_key, @policy_version, @side,
                    @position_intent, @quantity, @order_type, @limit_price,
                    @trigger_price, @time_in_force, @client_order_id,
                    @authorized_at_utc, @authorized_at_utc, @valid_until_utc,
                    @correlation_id, @raw_result_json, @result_hash, @actor
                );

                SET @execution_command_id = SCOPE_IDENTITY();

                INSERT INTO [execution].[execution_command_events]
                (
                    [execution_command_event_uid], [execution_command_id], [event_sequence],
                    [event_type], [command_status], [outcome_classification],
                    [occurred_at_utc], [source_service], [source_version],
                    [correlation_id], [created_by]
                )
                VALUES
                (
                    NEWID(), @execution_command_id, 0,
                    'PERSISTED', 'PERSISTED', 'NONE',
                    @authorized_at_utc, @source_service, @source_version,
                    @correlation_id, @actor
                );

                INSERT INTO [execution].[execution_command_states]
                (
                    [execution_command_id], [current_status], [outcome_classification],
                    [last_event_sequence], [can_retry_without_reconciliation],
                    [broker_contacted], [reconciliation_required], [updated_by]
                )
                VALUES
                (
                    @execution_command_id, 'PERSISTED', 'NONE', 0, 0, 0, 0, @actor
                );

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
                VALUES
                (
                    @order_uid, @execution_command_id, @trade_plan_id,
                    @broker_account_id, @instrument_id, 'PAPER', @side,
                    @position_intent, @quantity, 0,
                    @quantity, NULL, @order_type,
                    @limit_price, @trigger_price, @time_in_force, @client_order_id,
                    'CREATED', @order_version, @authorized_at_utc,
                    0, 0, @actor, @actor
                );

                SET @order_id = SCOPE_IDENTITY();

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
                    @order_uid, @order_uid, @order_id, @execution_command_id,
                    @trade_plan_id, @broker_account_id, @instrument_id, '1.0.0',
                    'PAPER', @source_service, @source_version, @side,
                    'CREATED', NULL, 'CREATED',
                    @quantity, 0, @quantity,
                    NULL, @authorized_at_utc, @authorized_at_utc,
                    @authorized_at_utc, @order_version, 0,
                    'ACCEPTED', @correlation_id, @raw_order_json,
                    @order_hash, @actor
                );

                UPDATE [execution].[execution_command_states]
                SET [result_order_uid] = @order_uid,
                    [updated_at_utc] = @authorized_at_utc,
                    [updated_by] = @actor
                WHERE [execution_command_id] = @execution_command_id;
                """;

            await using var command = CreateCommand(connection, transaction, sql);
            AddAuthorizationParameters(
                command,
                result,
                commandContract,
                orderContract,
                instrument,
                rawResultJson,
                rawOrderJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await FindAuthorizationAsync(
                commandContract.IdempotencyKey,
                cancellationToken);
            return existing is null
                ? throw new PaperExecutionIdempotencyConflictException(
                    "Execution command uniqueness was violated.")
                : ValidateReplay(existing, commandContract);
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
        return (await ReadOrderRowAsync(
            connection,
            null,
            paperOrderUid,
            lockForUpdate: false,
            cancellationToken))?.Snapshot;
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
            if (replay.Found)
            {
                var replayOrder = await ReadOrderRowAsync(
                    connection,
                    transaction,
                    paperOrderUid,
                    lockForUpdate: true,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PaperOrderTransitionResultV1(
                    true,
                    true,
                    Array.Empty<string>(),
                    replayOrder?.Snapshot,
                    request.OccurredAtUtc,
                    replay.FillUid);
            }

            var row = await ReadOrderRowAsync(
                connection,
                transaction,
                paperOrderUid,
                lockForUpdate: true,
                cancellationToken);
            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PaperOrderTransitionResultV1(
                    false,
                    false,
                    new[] { "PAPER_ORDER_NOT_FOUND" },
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
            var updateCount = await UpdateOrderAsync(
                connection,
                transaction,
                row,
                updated,
                cancellationToken);
            if (updateCount != 1)
            {
                throw new PaperExecutionConcurrencyException(
                    "The PAPER order changed while the event was being applied.");
            }

            await InsertOrderEventAsync(
                connection,
                transaction,
                row,
                updated,
                request,
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

    private async Task<ExecutionCommandResultV1?> FindAuthorizationCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string idempotencyKey,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var lockHint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT TOP (1) ec.[raw_contract_json]
            FROM [execution].[execution_commands] ec{lockHint}
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
            : JsonSerializer.Deserialize<ExecutionCommandResultV1>(
                Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                JsonOptions)
                ?? throw new InvalidOperationException(
                    "Stored execution command payload could not be deserialized.");
    }

    private static ExecutionCommandResultV1 ValidateReplay(
        ExecutionCommandResultV1 existing,
        ExecutionCommandV1 requested)
    {
        if (existing.Command?.TradePlanUid == requested.TradePlanUid &&
            string.Equals(
                existing.Command.CorrelationId,
                requested.CorrelationId,
                StringComparison.Ordinal))
        {
            return existing;
        }

        throw new PaperExecutionIdempotencyConflictException(
            "The idempotency key is already bound to another Trade Plan.");
    }

    private void AddAuthorizationParameters(
        SqlCommand command,
        ExecutionCommandResultV1 result,
        ExecutionCommandV1 contract,
        PaperOrderSnapshotV1 order,
        InstrumentKey instrument,
        string rawResultJson,
        string rawOrderJson)
    {
        command.Parameters.Add("@trade_plan_uid", SqlDbType.UniqueIdentifier).Value =
            contract.TradePlanUid;
        command.Parameters.Add("@risk_decision_uid", SqlDbType.UniqueIdentifier).Value =
            contract.RiskDecisionUid;
        command.Parameters.Add("@thesis_uid", SqlDbType.UniqueIdentifier).Value = contract.ThesisUid;
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = contract.SignalUid;
        command.Parameters.Add("@exchange_code", SqlDbType.VarChar, 20).Value =
            instrument.ExchangeCode;
        command.Parameters.Add("@instrument_value", SqlDbType.VarChar, 100).Value =
            instrument.LookupValue;
        command.Parameters.Add("@account_reference", SqlDbType.VarChar, 100).Value =
            _options.BrokerAccountReference;
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value =
            contract.ExecutionCommandUid;
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value = contract.RequestUid;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            contract.IdempotencyKey;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 50).Value =
            contract.ExecutionPolicyVersion;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = contract.Side;
        command.Parameters.Add("@position_intent", SqlDbType.VarChar, 20).Value =
            contract.PositionIntent;
        AddDecimal(command, "@quantity", contract.Quantity, 19, 6);
        command.Parameters.Add("@order_type", SqlDbType.VarChar, 20).Value =
            contract.Entry.OrderType;
        AddNullableDecimal(command, "@limit_price", contract.Entry.LimitPrice, 19, 6);
        AddNullableDecimal(command, "@trigger_price", contract.Entry.TriggerPrice, 19, 6);
        command.Parameters.Add("@time_in_force", SqlDbType.VarChar, 10).Value =
            contract.TimeInForce;
        command.Parameters.Add("@client_order_id", SqlDbType.VarChar, 100).Value =
            order.PaperOrderUid.ToString("N");
        AddDateTime(command, "@authorized_at_utc", contract.AuthorizedAtUtc);
        AddDateTime(command, "@valid_until_utc", contract.ValidUntilUtc);
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                contract.CorrelationId,
                nameof(contract.CorrelationId));
        command.Parameters.Add("@raw_result_json", SqlDbType.NVarChar, -1).Value = rawResultJson;
        command.Parameters.Add("@result_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawResultJson);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = order.PaperOrderUid;
        command.Parameters.Add("@order_version", SqlDbType.Int).Value = order.Version;
        command.Parameters.Add("@raw_order_json", SqlDbType.NVarChar, -1).Value = rawOrderJson;
        command.Parameters.Add("@order_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawOrderJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }

    private async Task<EventReplay> FindEventReplayAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid eventUid,
        Guid orderUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                CAST(1 AS bit), f.[fill_uid]
            FROM [execution].[order_events] oe WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [execution].[orders] o ON o.[order_id] = oe.[order_id]
            LEFT JOIN [execution].[fills] f ON f.[fill_uid] = oe.[order_event_uid]
            WHERE oe.[order_event_uid] = @event_uid
              AND o.[order_uid] = @order_uid;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value = eventUid;
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EventReplay(
                true,
                reader.IsDBNull(1) ? null : reader.GetGuid(1))
            : new EventReplay(false, null);
    }

    private async Task<OrderRow?> ReadOrderRowAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid orderUid,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var lockHint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT
                o.[order_id], o.[place_execution_command_id], o.[trade_plan_id],
                o.[broker_account_id], o.[instrument_id], o.[order_uid],
                ec.[execution_command_uid], tp.[trade_plan_uid],
                COALESCE(rd.[risk_decision_uid], sre.[risk_decision_uid]),
                COALESCE(th.[thesis_uid], candidate_lineage.[thesis_uid]), s.[signal_uid],
                tp.[correlation_id], ec.[idempotency_key], o.[environment],
                e.[exchange_code], i.[canonical_symbol], o.[side], o.[current_status],
                o.[requested_quantity], o.[filled_quantity], o.[remaining_quantity],
                o.[average_fill_price], tp.[allow_partial_fill], o.[current_order_version],
                o.[created_at_utc], o.[updated_at_utc],
                CASE WHEN o.[is_terminal] = 1 THEN o.[updated_at_utc] ELSE NULL END
            FROM [execution].[orders] o{lockHint}
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [risk].[trade_plans] tp
                ON tp.[trade_plan_id] = o.[trade_plan_id]
            LEFT JOIN [risk].[risk_decisions] rd
                ON rd.[risk_decision_id] = tp.[risk_decision_id]
            LEFT JOIN [risk].[signal_risk_evaluations] sre
                ON sre.[signal_risk_evaluation_id] = tp.[signal_risk_evaluation_id]
            LEFT JOIN [thesis].[theses] th
                ON th.[thesis_id] = tp.[thesis_id]
            LEFT JOIN [intelligence].[signal_fusion_lineage] candidate_lineage
                ON candidate_lineage.[signal_id] = tp.[signal_id]
               AND candidate_lineage.[thesis_uid] = tp.[candidate_thesis_uid]
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = tp.[signal_id]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = o.[instrument_id]
            INNER JOIN [reference].[exchanges] e ON e.[exchange_id] = i.[exchange_id]
            WHERE o.[order_uid] = @order_uid
              AND COALESCE(rd.[risk_decision_uid], sre.[risk_decision_uid]) IS NOT NULL
              AND COALESCE(th.[thesis_uid], candidate_lineage.[thesis_uid]) IS NOT NULL;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = orderUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var side = reader.GetString(16);
        var snapshot = new PaperOrderSnapshotV1(
            reader.GetGuid(5),
            reader.GetGuid(6),
            reader.GetGuid(7),
            reader.GetGuid(8),
            reader.GetGuid(9),
            reader.GetGuid(10),
            reader.GetGuid(11).ToString("D"),
            reader.GetString(12),
            reader.GetString(13),
            $"{reader.GetString(14)}|{reader.GetString(15)}",
            side == "BUY" ? EvidenceDirectionV1.Long : EvidenceDirectionV1.Short,
            side,
            reader.GetString(17),
            reader.GetDecimal(18),
            reader.GetDecimal(19),
            reader.GetDecimal(20),
            reader.IsDBNull(21) ? null : reader.GetDecimal(21),
            reader.GetBoolean(22),
            reader.GetInt32(23),
            ReadUtc(reader, 24),
            ReadUtc(reader, 25),
            reader.IsDBNull(26) ? null : ReadUtc(reader, 26),
            null);
        return new OrderRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            snapshot);
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
        OrderRow row,
        PaperOrderSnapshotV1 updated,
        PaperOrderEventRequestV1 request,
        CancellationToken cancellationToken)
    {
        var rawJson = JsonSerializer.Serialize(request, JsonOptions);
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
        command.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value = request.EventUid;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = row.OrderId;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = row.ExecutionCommandId;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = row.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = row.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = row.InstrumentId;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = updated.Side;
        command.Parameters.Add("@event_type", SqlDbType.VarChar, 30).Value = updated.State;
        command.Parameters.Add("@previous_status", SqlDbType.VarChar, 30).Value =
            row.Snapshot.State;
        command.Parameters.Add("@normalized_status", SqlDbType.VarChar, 30).Value = updated.State;
        AddDecimal(command, "@requested_quantity", updated.RequestedQuantity, 19, 6);
        AddDecimal(command, "@filled_quantity", updated.FilledQuantity, 19, 6);
        AddDecimal(command, "@remaining_quantity", updated.RemainingQuantity, 19, 6);
        AddNullableDecimal(command, "@average_fill_price", updated.AverageFillPrice, 19, 6);
        AddDateTime(command, "@event_at_utc", request.OccurredAtUtc);
        command.Parameters.Add("@order_version", SqlDbType.Int).Value = updated.Version;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                updated.CorrelationId,
                nameof(updated.CorrelationId));
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
        var quantity = request.FillQuantity
            ?? throw new InvalidOperationException("Applied fill has no quantity.");
        var price = request.FillPrice
            ?? throw new InvalidOperationException("Applied fill has no price.");
        var rawJson = JsonSerializer.Serialize(request, JsonOptions);
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
                'PAPER', @source_service, @source_version, @fill_fingerprint,
                @side, @quantity, @price, @gross, 0,
                0, @gross, @currency_code, 'UNKNOWN',
                @fill_at_utc, @fill_at_utc, @fill_at_utc,
                0, @correlation_id, @raw_json,
                @contract_hash, @actor
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = request.EventUid;
        command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = row.OrderId;
        command.Parameters.Add("@command_id", SqlDbType.BigInt).Value = row.ExecutionCommandId;
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = row.TradePlanId;
        command.Parameters.Add("@broker_account_id", SqlDbType.BigInt).Value = row.BrokerAccountId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = row.InstrumentId;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@fill_fingerprint", SqlDbType.VarChar, 256).Value =
            $"PAPER:{row.Snapshot.PaperOrderUid:N}:{request.EventUid:N}";
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = row.Snapshot.Side;
        AddDecimal(command, "@quantity", quantity, 19, 6);
        AddDecimal(command, "@price", price, 19, 6);
        AddDecimal(command, "@gross", gross, 19, 6);
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value =
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
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static InstrumentKey ParseInstrumentKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException(
                "Instrument key format is invalid.",
                nameof(value));
        }

        var exchangeToken = parts[0];
        var separatorIndex = exchangeToken.IndexOf('_');
        var exchangeCode = separatorIndex > 0
            ? exchangeToken[..separatorIndex]
            : exchangeToken;
        return new InstrumentKey(
            exchangeCode.Trim().ToUpperInvariant(),
            parts[1].Trim());
    }

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

    private sealed record InstrumentKey(string ExchangeCode, string LookupValue);

    private sealed record EventReplay(bool Found, Guid? FillUid);

    private sealed record OrderRow(
        long OrderId,
        long ExecutionCommandId,
        long TradePlanId,
        long BrokerAccountId,
        long InstrumentId,
        PaperOrderSnapshotV1 Snapshot);
}
