using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public sealed class SqlServerAutomaticPaperFillCandidateStore(
    SqlServerPaperExecutionLedgerOptions ledgerOptions,
    AutomaticPaperFillOptions fillOptions) : IAutomaticPaperFillCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<AutomaticPaperFillCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                o.[order_id], o.[order_uid],
                ec.[execution_command_id], ec.[execution_command_uid],
                ec.[correlation_id], mapping.[broker_instrument_key],
                o.[updated_at_utc], ec.[raw_contract_json]
            FROM [execution].[orders] o WITH (READPAST)
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [execution].[execution_command_states] ecs
                ON ecs.[execution_command_id] = ec.[execution_command_id]
            INNER JOIN [reference].[broker_instrument_mappings] mapping
                ON mapping.[instrument_id] = o.[instrument_id]
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            WHERE o.[environment] = 'PAPER'
              AND o.[current_status] = 'ACKNOWLEDGED'
              AND o.[is_terminal] = 0
              AND o.[reconciliation_required] = 0
              AND o.[filled_quantity] = 0
              AND o.[remaining_quantity] = o.[requested_quantity]
              AND o.[broker_order_id] IS NOT NULL
              AND ec.[environment] = 'PAPER'
              AND ec.[command_type] = 'PLACE'
              AND ecs.[current_status] = 'ACKNOWLEDGED'
              AND ecs.[reconciliation_required] = 0
              AND mapping.[is_active] = 1
              AND broker.[broker_code] = @broker_code
              AND ISJSON(ec.[raw_contract_json]) = 1
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [execution].[paper_fill_work_items] w
                  WHERE w.[order_id] = o.[order_id]
              )
            ORDER BY o.[updated_at_utc], o.[order_id];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            fillOptions.MarketDataBrokerCode;

        var candidates = new List<AutomaticPaperFillCandidate>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var result = JsonSerializer.Deserialize<ExecutionCommandResultV1>(
                reader.GetString(7),
                JsonOptions)
                ?? throw new InvalidOperationException(
                    "Stored execution authorization could not be deserialized for PAPER fill.");
            var commandContract = result.Command
                ?? throw new InvalidOperationException(
                    "Stored execution authorization does not contain a command.");
            if (commandContract.ExecutionCommandUid != reader.GetGuid(3))
                throw new InvalidOperationException("Execution command UID does not match stored payload.");

            candidates.Add(new AutomaticPaperFillCandidate(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetGuid(3),
                reader.GetGuid(4).ToString("D"),
                reader.GetString(5),
                ReadUtc(reader, 6),
                commandContract));
        }
        return candidates;
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

public sealed class SqlServerAutomaticPaperFillWorkQueue(
    SqlServerPaperExecutionLedgerOptions ledgerOptions,
    AutomaticPaperFillOptions fillOptions) : IAutomaticPaperFillWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AutomaticPaperFillEnqueueResult> EnqueueAsync(
        AutomaticPaperFillCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reasons = Validate(candidate);
        if (reasons.Count > 0)
        {
            return new AutomaticPaperFillEnqueueResult(
                AutomaticPaperFillStatus.Rejected,
                candidate.OrderUid,
                reasons);
        }

        const string sql = """
            DECLARE @resolved_order_id bigint;
            SELECT @resolved_order_id = o.[order_id]
            FROM [execution].[orders] o WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [execution].[execution_command_states] ecs
                ON ecs.[execution_command_id] = ec.[execution_command_id]
            WHERE o.[order_id] = @order_id
              AND o.[order_uid] = @order_uid
              AND o.[place_execution_command_id] = @execution_command_id
              AND ec.[execution_command_uid] = @execution_command_uid
              AND ec.[correlation_id] = @correlation_id
              AND o.[environment] = 'PAPER'
              AND o.[current_status] = 'ACKNOWLEDGED'
              AND o.[is_terminal] = 0
              AND o.[filled_quantity] = 0
              AND o.[remaining_quantity] = o.[requested_quantity]
              AND o.[broker_order_id] IS NOT NULL
              AND ecs.[current_status] = 'ACKNOWLEDGED'
              AND ecs.[reconciliation_required] = 0;

            IF @resolved_order_id IS NULL
            BEGIN
                SELECT CAST(-1 AS int);
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [execution].[paper_fill_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [order_id] = @resolved_order_id OR [order_uid] = @order_uid
            )
            BEGIN
                SELECT CAST(0 AS int);
                RETURN;
            END;

            INSERT INTO [execution].[paper_fill_work_items]
            (
                [order_id], [order_uid], [execution_command_id], [execution_command_uid],
                [correlation_id], [provider_instrument_key], [eligible_after_utc],
                [fill_policy_version], [payload_json], [current_status],
                [next_attempt_at_utc]
            )
            VALUES
            (
                @resolved_order_id, @order_uid, @execution_command_id, @execution_command_uid,
                @correlation_id, @provider_instrument_key, @eligible_after_utc,
                @fill_policy_version, @payload_json, 'PENDING', SYSUTCDATETIME()
            );
            SELECT CAST(1 AS int);
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = candidate.OrderId;
            command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = candidate.OrderUid;
            command.Parameters.Add("@execution_command_id", SqlDbType.BigInt).Value = candidate.ExecutionCommandId;
            command.Parameters.Add("@execution_command_uid", SqlDbType.UniqueIdentifier).Value = candidate.ExecutionCommandUid;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(candidate.CorrelationId);
            command.Parameters.Add("@provider_instrument_key", SqlDbType.VarChar, 200).Value = candidate.ProviderInstrumentKey;
            command.Parameters.Add("@eligible_after_utc", SqlDbType.DateTime2).Value = candidate.EligibleAfterUtc.UtcDateTime;
            command.Parameters.Add("@fill_policy_version", SqlDbType.VarChar, 50).Value = fillOptions.FillPolicyVersion;
            command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(candidate.Command, JsonOptions);
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return outcome switch
            {
                1 => new AutomaticPaperFillEnqueueResult("ENQUEUED", candidate.OrderUid, Array.Empty<string>()),
                0 => new AutomaticPaperFillEnqueueResult("DUPLICATE", candidate.OrderUid, Array.Empty<string>()),
                _ => new AutomaticPaperFillEnqueueResult(
                    AutomaticPaperFillStatus.Rejected,
                    candidate.OrderUid,
                    new[] { "AUTHORITATIVE_ACKNOWLEDGED_ORDER_NOT_FOUND" }),
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPaperFillWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [execution].[paper_fill_work_items]
                    WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING','DEFERRED','RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [paper_fill_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [evaluation_count] = [evaluation_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[paper_fill_work_item_id], INSERTED.[order_id],
                   INSERTED.[order_uid], INSERTED.[execution_command_id],
                   INSERTED.[execution_command_uid], INSERTED.[correlation_id],
                   INSERTED.[provider_instrument_key], INSERTED.[eligible_after_utc],
                   INSERTED.[payload_json], INSERTED.[last_evaluated_candle_id],
                   INSERTED.[last_evaluated_candle_uid], INSERTED.[last_evaluated_close_at_utc],
                   INSERTED.[evaluation_count], INSERTED.[error_count];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;

        var items = new List<AutomaticPaperFillWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var commandContract = JsonSerializer.Deserialize<ExecutionCommandV1>(
                reader.GetString(8),
                JsonOptions)
                ?? throw new InvalidOperationException("PAPER fill work payload could not be deserialized.");
            items.Add(new AutomaticPaperFillWorkItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetGuid(4),
                reader.GetGuid(5).ToString("D"),
                reader.GetString(6),
                ReadUtc(reader, 7),
                commandContract,
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.IsDBNull(11) ? null : ReadUtc(reader, 11),
                reader.GetInt32(12),
                reader.GetInt32(13)));
        }
        return items;
    }

    public async Task CompleteAsync(
        long workItemId,
        Guid? fillEventUid,
        decimal? fillPrice,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @order_id bigint;
            DECLARE @execution_command_id bigint;
            DECLARE @correlation_id uniqueidentifier;
            DECLARE @resolved_fill_uid uniqueidentifier;
            DECLARE @resolved_fill_price decimal(19,6);
            DECLARE @fill_at_utc datetime2(7);

            SELECT
                @order_id = [order_id],
                @execution_command_id = [execution_command_id],
                @correlation_id = [correlation_id]
            FROM [execution].[paper_fill_work_items] WITH (UPDLOCK, HOLDLOCK)
            WHERE [paper_fill_work_item_id] = @work_item_id
              AND [current_status] IN ('LEASED','FILLED');

            IF @order_id IS NULL
                THROW 59510, 'Automatic PAPER fill work item was not found.', 1;

            SELECT TOP (1)
                @resolved_fill_uid = f.[fill_uid],
                @resolved_fill_price = f.[fill_price],
                @fill_at_utc = f.[fill_at_utc]
            FROM [execution].[fills] f WITH (UPDLOCK, HOLDLOCK)
            WHERE f.[order_id] = @order_id
              AND (@fill_event_uid IS NULL OR f.[fill_uid] = @fill_event_uid)
            ORDER BY f.[fill_at_utc], f.[fill_id];

            IF @resolved_fill_uid IS NULL
                THROW 59511, 'Authoritative PAPER fill was not found.', 1;

            IF @fill_price IS NOT NULL AND @resolved_fill_price <> @fill_price
                THROW 59512, 'Authoritative PAPER fill price does not match the work result.', 1;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [execution].[orders]
                WHERE [order_id] = @order_id
                  AND [environment] = 'PAPER'
                  AND [current_status] = 'FILLED'
                  AND [is_terminal] = 1
                  AND [remaining_quantity] = 0
                  AND [filled_quantity] = [requested_quantity]
                  AND [average_fill_price] = @resolved_fill_price
            )
                THROW 59513, 'FILLED PAPER order invariant was not satisfied.', 1;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [execution].[execution_command_events] WITH (UPDLOCK, HOLDLOCK)
                WHERE [execution_command_id] = @execution_command_id
                  AND [event_sequence] = 2
            )
            BEGIN
                INSERT INTO [execution].[execution_command_events]
                (
                    [execution_command_event_uid], [execution_command_id], [event_sequence],
                    [event_type], [command_status], [outcome_classification],
                    [occurred_at_utc], [source_service], [source_version],
                    [correlation_id], [created_by]
                )
                VALUES
                (
                    @resolved_fill_uid, @execution_command_id, 2,
                    'COMPLETED', 'COMPLETED', 'NONE',
                    @fill_at_utc, @actor, @source_version,
                    @correlation_id, @actor
                );
            END;

            UPDATE [execution].[execution_command_states]
            SET [current_status] = 'COMPLETED',
                [outcome_classification] = 'NONE',
                [last_event_sequence] = CASE WHEN [last_event_sequence] < 2 THEN 2 ELSE [last_event_sequence] END,
                [can_retry_without_reconciliation] = 0,
                [reconciliation_required] = 0,
                [last_error_code] = NULL,
                [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [execution_command_id] = @execution_command_id;

            UPDATE [execution].[paper_fill_work_items]
            SET [current_status] = 'FILLED',
                [fill_event_uid] = @resolved_fill_uid,
                [fill_uid] = @resolved_fill_uid,
                [fill_price] = @resolved_fill_price,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [error_count] = 0,
                [reasons_json] = N'["DETERMINISTIC_PAPER_FILL_COMPLETED"]',
                [last_error] = NULL,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_fill_work_item_id] = @work_item_id;
            """;

        await ExecuteTransactionAsync(sql, command =>
        {
            command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
            command.Parameters.Add("@fill_event_uid", SqlDbType.UniqueIdentifier).Value =
                fillEventUid.HasValue ? fillEventUid.Value : DBNull.Value;
            var priceParameter = command.Parameters.Add("@fill_price", SqlDbType.Decimal);
            priceParameter.Precision = 19;
            priceParameter.Scale = 6;
            priceParameter.Value = fillPrice.HasValue ? fillPrice.Value : DBNull.Value;
            AddActorParameters(command);
        }, cancellationToken);
    }

    public Task DeferAsync(
        long workItemId,
        StoredCandleV1? lastEvaluatedCandle,
        DateTimeOffset availableAtUtc,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperFillStatus.Deferred,
            availableAtUtc,
            reasons,
            null,
            lastEvaluatedCandle,
            resetErrorCount: true,
            cancellationToken);

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperFillStatus.RetryPending,
            availableAtUtc,
            new[] { "PAPER_FILL_TRANSIENT_FAILURE" },
            error,
            null,
            resetErrorCount: false,
            cancellationToken);

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperFillStatus.Rejected,
            null,
            reasons,
            null,
            null,
            resetErrorCount: false,
            cancellationToken);

    public async Task ExpireAsync(
        long workItemId,
        Guid expireEventUid,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @order_id bigint;
            DECLARE @execution_command_id bigint;
            DECLARE @correlation_id uniqueidentifier;
            DECLARE @expired_at_utc datetime2(7);

            SELECT
                @order_id = [order_id],
                @execution_command_id = [execution_command_id],
                @correlation_id = [correlation_id]
            FROM [execution].[paper_fill_work_items] WITH (UPDLOCK, HOLDLOCK)
            WHERE [paper_fill_work_item_id] = @work_item_id
              AND [current_status] IN ('LEASED','EXPIRED');

            IF @order_id IS NULL
                THROW 59520, 'Automatic PAPER fill expiry work item was not found.', 1;

            SELECT @expired_at_utc = [updated_at_utc]
            FROM [execution].[orders]
            WHERE [order_id] = @order_id
              AND [environment] = 'PAPER'
              AND [current_status] = 'EXPIRED'
              AND [is_terminal] = 1
              AND [filled_quantity] = 0;

            IF @expired_at_utc IS NULL
                THROW 59521, 'EXPIRED PAPER order invariant was not satisfied.', 1;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [execution].[execution_command_events] WITH (UPDLOCK, HOLDLOCK)
                WHERE [execution_command_id] = @execution_command_id
                  AND [event_sequence] = 2
            )
            BEGIN
                INSERT INTO [execution].[execution_command_events]
                (
                    [execution_command_event_uid], [execution_command_id], [event_sequence],
                    [event_type], [command_status], [outcome_classification],
                    [reason_code], [reason_message], [occurred_at_utc],
                    [source_service], [source_version], [correlation_id], [created_by]
                )
                VALUES
                (
                    @expire_event_uid, @execution_command_id, 2,
                    'EXPIRED', 'EXPIRED', 'SESSION_RESTRICTION',
                    @reason, @reason, @expired_at_utc,
                    @actor, @source_version, @correlation_id, @actor
                );
            END;

            UPDATE [execution].[execution_command_states]
            SET [current_status] = 'EXPIRED',
                [outcome_classification] = 'SESSION_RESTRICTION',
                [last_event_sequence] = CASE WHEN [last_event_sequence] < 2 THEN 2 ELSE [last_event_sequence] END,
                [can_retry_without_reconciliation] = 0,
                [reconciliation_required] = 0,
                [last_error_code] = @reason,
                [last_error_message] = @reason,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [execution_command_id] = @execution_command_id;

            UPDATE [execution].[paper_fill_work_items]
            SET [current_status] = 'EXPIRED',
                [expire_event_uid] = @expire_event_uid,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = @reasons_json,
                [last_error] = NULL,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_fill_work_item_id] = @work_item_id;
            """;

        await ExecuteTransactionAsync(sql, command =>
        {
            command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
            command.Parameters.Add("@expire_event_uid", SqlDbType.UniqueIdentifier).Value = expireEventUid;
            command.Parameters.Add("@reason", SqlDbType.VarChar, 100).Value = reason;
            command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(new[] { reason }, JsonOptions);
            AddActorParameters(command);
        }, cancellationToken);
    }

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperFillStatus.Failed,
            null,
            new[] { "PAPER_FILL_FAILED" },
            error,
            null,
            resetErrorCount: false,
            cancellationToken);

    private async Task UpdateAsync(
        long workItemId,
        string status,
        DateTimeOffset? availableAtUtc,
        IReadOnlyCollection<string> reasons,
        string? error,
        StoredCandleV1? lastEvaluatedCandle,
        bool resetErrorCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [execution].[paper_fill_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_evaluated_candle_id] = COALESCE(@candle_id, [last_evaluated_candle_id]),
                [last_evaluated_candle_uid] = COALESCE(@candle_uid, [last_evaluated_candle_uid]),
                [last_evaluated_close_at_utc] = COALESCE(@candle_close_at_utc, [last_evaluated_close_at_utc]),
                [error_count] = CASE WHEN @reset_error_count = 1 THEN 0 ELSE [error_count] + CASE WHEN @status = 'RETRY_PENDING' THEN 1 ELSE 0 END END,
                [reasons_json] = @reasons_json,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_fill_work_item_id] = @work_item_id;
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@next_attempt_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@candle_id", SqlDbType.BigInt).Value =
            lastEvaluatedCandle is null ? DBNull.Value : lastEvaluatedCandle.CandleId;
        command.Parameters.Add("@candle_uid", SqlDbType.UniqueIdentifier).Value =
            lastEvaluatedCandle is null ? DBNull.Value : lastEvaluatedCandle.CandleUid;
        command.Parameters.Add("@candle_close_at_utc", SqlDbType.DateTime2).Value =
            lastEvaluatedCandle is null ? DBNull.Value : lastEvaluatedCandle.CloseAtUtc.UtcDateTime;
        command.Parameters.Add("@reset_error_count", SqlDbType.Bit).Value = resetErrorCount;
        command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(reasons, JsonOptions);
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteTransactionAsync(
        string sql,
        Action<SqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            configure(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private void AddActorParameters(SqlCommand command)
    {
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = ledgerOptions.SourceVersion;
    }

    private static IReadOnlyCollection<string> Validate(AutomaticPaperFillCandidate candidate)
    {
        var reasons = new List<string>();
        if (candidate.OrderId <= 0 || candidate.ExecutionCommandId <= 0)
            reasons.Add("PERSISTED_ORDER_IDENTITY_REQUIRED");
        if (candidate.OrderUid == Guid.Empty || candidate.ExecutionCommandUid == Guid.Empty)
            reasons.Add("ORDER_LINEAGE_REQUIRED");
        if (!Guid.TryParse(candidate.CorrelationId, out _))
            reasons.Add("CORRELATION_ID_INVALID");
        if (string.IsNullOrWhiteSpace(candidate.ProviderInstrumentKey))
            reasons.Add("PROVIDER_INSTRUMENT_KEY_REQUIRED");
        if (candidate.Command.ExecutionCommandUid != candidate.ExecutionCommandUid ||
            candidate.Command.TradePlanUid == Guid.Empty)
            reasons.Add("COMMAND_PAYLOAD_LINEAGE_INVALID");
        if (!string.Equals(candidate.Command.Environment, "PAPER", StringComparison.OrdinalIgnoreCase))
            reasons.Add("PAPER_ENVIRONMENT_REQUIRED");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
