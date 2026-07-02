using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public sealed class SqlServerAutomaticPaperSubmissionCandidateStore(
    SqlServerPaperExecutionLedgerOptions options) : IAutomaticPaperSubmissionCandidateStore
{
    public async Task<IReadOnlyCollection<AutomaticPaperSubmissionCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                o.[order_id], o.[order_uid],
                ec.[execution_command_id], ec.[execution_command_uid],
                ec.[correlation_id], ec.[valid_until_utc]
            FROM [execution].[orders] o WITH (READPAST)
            INNER JOIN [execution].[execution_commands] ec
                ON ec.[execution_command_id] = o.[place_execution_command_id]
            INNER JOIN [execution].[execution_command_states] ecs
                ON ecs.[execution_command_id] = ec.[execution_command_id]
            WHERE o.[environment] = 'PAPER'
              AND o.[current_status] = 'CREATED'
              AND o.[is_terminal] = 0
              AND o.[reconciliation_required] = 0
              AND ec.[environment] = 'PAPER'
              AND ec.[command_type] = 'PLACE'
              AND ec.[valid_until_utc] > @as_of_utc
              AND ecs.[current_status] = 'PERSISTED'
              AND ecs.[broker_contacted] = 0
              AND ecs.[reconciliation_required] = 0
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [execution].[paper_submission_work_items] w
                  WHERE w.[order_id] = o.[order_id]
              )
            ORDER BY o.[created_at_utc], o.[order_id];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;

        var candidates = new List<AutomaticPaperSubmissionCandidate>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new AutomaticPaperSubmissionCandidate(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetGuid(3),
                reader.GetGuid(4).ToString("D"),
                ReadUtc(reader, 5)));
        }
        return candidates;
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

public sealed class SqlServerAutomaticPaperSubmissionWorkQueue(
    SqlServerPaperExecutionLedgerOptions options) : IAutomaticPaperSubmissionWorkQueue
{
    public async Task<AutomaticPaperSubmissionEnqueueResult> EnqueueAsync(
        AutomaticPaperSubmissionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var submitEventUid = AutomaticPaperSubmissionIdentity.SubmitEventUid(candidate.OrderUid);
        var acknowledgeEventUid =
            AutomaticPaperSubmissionIdentity.AcknowledgeEventUid(candidate.OrderUid);
        var expireEventUid = AutomaticPaperSubmissionIdentity.ExpireEventUid(candidate.OrderUid);
        var brokerOrderId = AutomaticPaperSubmissionIdentity.BrokerOrderId(candidate.OrderUid);
        var reasons = Validate(candidate);
        if (reasons.Count > 0)
        {
            return new AutomaticPaperSubmissionEnqueueResult(
                AutomaticPaperSubmissionStatus.Rejected,
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
              AND o.[current_status] = 'CREATED'
              AND o.[is_terminal] = 0
              AND o.[reconciliation_required] = 0
              AND ec.[environment] = 'PAPER'
              AND ec.[command_type] = 'PLACE'
              AND ec.[valid_until_utc] > SYSUTCDATETIME()
              AND ecs.[current_status] = 'PERSISTED'
              AND ecs.[broker_contacted] = 0
              AND ecs.[reconciliation_required] = 0;

            IF @resolved_order_id IS NULL
            BEGIN
                SELECT CAST(-1 AS int);
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [execution].[paper_submission_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [order_id] = @resolved_order_id
                   OR [order_uid] = @order_uid
                   OR [submit_event_uid] = @submit_event_uid
                   OR [acknowledge_event_uid] = @acknowledge_event_uid
                   OR [broker_order_id] = @broker_order_id
            )
            BEGIN
                SELECT CAST(0 AS int);
                RETURN;
            END;

            INSERT INTO [execution].[paper_submission_work_items]
            (
                [order_id], [order_uid], [execution_command_id], [execution_command_uid],
                [correlation_id], [valid_until_utc], [submit_event_uid],
                [acknowledge_event_uid], [expire_event_uid], [broker_order_id],
                [current_status], [next_attempt_at_utc]
            )
            VALUES
            (
                @resolved_order_id, @order_uid, @execution_command_id, @execution_command_uid,
                @correlation_id, @valid_until_utc, @submit_event_uid,
                @acknowledge_event_uid, @expire_event_uid, @broker_order_id,
                'PENDING', SYSUTCDATETIME()
            );
            SELECT CAST(1 AS int);
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@order_id", SqlDbType.BigInt).Value = candidate.OrderId;
            command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value = candidate.OrderUid;
            command.Parameters.Add("@execution_command_id", SqlDbType.BigInt).Value =
                candidate.ExecutionCommandId;
            command.Parameters.Add("@execution_command_uid", SqlDbType.UniqueIdentifier).Value =
                candidate.ExecutionCommandUid;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
                Guid.Parse(candidate.CorrelationId);
            command.Parameters.Add("@valid_until_utc", SqlDbType.DateTime2).Value =
                candidate.ValidUntilUtc.UtcDateTime;
            command.Parameters.Add("@submit_event_uid", SqlDbType.UniqueIdentifier).Value =
                submitEventUid;
            command.Parameters.Add("@acknowledge_event_uid", SqlDbType.UniqueIdentifier).Value =
                acknowledgeEventUid;
            command.Parameters.Add("@expire_event_uid", SqlDbType.UniqueIdentifier).Value =
                expireEventUid;
            command.Parameters.Add("@broker_order_id", SqlDbType.VarChar, 200).Value =
                brokerOrderId;
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return outcome switch
            {
                1 => new AutomaticPaperSubmissionEnqueueResult(
                    "ENQUEUED", candidate.OrderUid, Array.Empty<string>()),
                0 => new AutomaticPaperSubmissionEnqueueResult(
                    "DUPLICATE", candidate.OrderUid, Array.Empty<string>()),
                _ => new AutomaticPaperSubmissionEnqueueResult(
                    AutomaticPaperSubmissionStatus.Expired,
                    candidate.OrderUid,
                    new[] { "AUTHORITATIVE_CREATED_ORDER_NOT_FOUND" }),
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPaperSubmissionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [execution].[paper_submission_work_items]
                    WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING','RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [paper_submission_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[paper_submission_work_item_id], INSERTED.[order_id],
                   INSERTED.[order_uid], INSERTED.[execution_command_id],
                   INSERTED.[execution_command_uid], INSERTED.[correlation_id],
                   INSERTED.[valid_until_utc], INSERTED.[submit_event_uid],
                   INSERTED.[acknowledge_event_uid], INSERTED.[expire_event_uid],
                   INSERTED.[broker_order_id], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value =
            (int)leaseDuration.TotalSeconds;

        var items = new List<AutomaticPaperSubmissionWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AutomaticPaperSubmissionWorkItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetGuid(4),
                reader.GetGuid(5).ToString("D"),
                ReadUtc(reader, 6),
                reader.GetGuid(7),
                reader.GetGuid(8),
                reader.GetGuid(9),
                reader.GetString(10),
                reader.GetInt32(11)));
        }
        return items;
    }

    public async Task AcknowledgeAsync(
        long workItemId,
        string brokerOrderId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @order_id bigint;
            DECLARE @execution_command_id bigint;
            DECLARE @submit_event_uid uniqueidentifier;
            DECLARE @acknowledge_event_uid uniqueidentifier;
            DECLARE @correlation_id uniqueidentifier;

            SELECT
                @order_id = [order_id],
                @execution_command_id = [execution_command_id],
                @submit_event_uid = [submit_event_uid],
                @acknowledge_event_uid = [acknowledge_event_uid],
                @correlation_id = [correlation_id]
            FROM [execution].[paper_submission_work_items] WITH (UPDLOCK, HOLDLOCK)
            WHERE [paper_submission_work_item_id] = @work_item_id
              AND [broker_order_id] = @broker_order_id
              AND [current_status] IN ('LEASED', 'ACKNOWLEDGED');

            IF @order_id IS NULL OR @execution_command_id IS NULL
                THROW 59410, 'Automatic PAPER submission work item was not found.', 1;

            UPDATE [execution].[orders]
            SET [broker_order_id] = @broker_order_id,
                [broker_client_tag] = 'INTERNAL_DETERMINISTIC_PAPER',
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [order_id] = @order_id
              AND [environment] = 'PAPER'
              AND [current_status] = 'ACKNOWLEDGED'
              AND [filled_quantity] = 0
              AND [remaining_quantity] = [requested_quantity]
              AND ([broker_order_id] IS NULL OR [broker_order_id] = @broker_order_id);

            IF @@ROWCOUNT <> 1
                THROW 59411, 'ACKNOWLEDGED PAPER order invariant was not satisfied.', 1;

            UPDATE [execution].[order_events]
            SET [broker_order_id] = @broker_order_id,
                [broker_status] = [normalized_status]
            WHERE [order_id] = @order_id
              AND [order_event_uid] IN (@submit_event_uid, @acknowledge_event_uid);

            IF NOT EXISTS
            (
                SELECT 1
                FROM [execution].[execution_command_events] WITH (UPDLOCK, HOLDLOCK)
                WHERE [execution_command_id] = @execution_command_id
                  AND [event_sequence] = 1
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
                    @acknowledge_event_uid, @execution_command_id, 1,
                    'SUBMISSION_SUCCEEDED', 'ACKNOWLEDGED', 'NONE',
                    SYSUTCDATETIME(), @actor, @source_version,
                    @correlation_id, @actor
                );
            END;

            UPDATE [execution].[execution_command_states]
            SET [current_status] = 'ACKNOWLEDGED',
                [outcome_classification] = 'NONE',
                [last_event_sequence] = CASE
                    WHEN [last_event_sequence] < 1 THEN 1
                    ELSE [last_event_sequence]
                END,
                [broker_contacted] = 0,
                [reconciliation_required] = 0,
                [can_retry_without_reconciliation] = 0,
                [last_error_code] = NULL,
                [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [execution_command_id] = @execution_command_id;

            UPDATE [execution].[paper_submission_work_items]
            SET [current_status] = 'ACKNOWLEDGED',
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = N'["INTERNAL_PAPER_GATEWAY_ACKNOWLEDGED"]',
                [last_error] = NULL,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_submission_work_item_id] = @work_item_id;
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
            command.Parameters.Add("@broker_order_id", SqlDbType.VarChar, 200).Value =
                brokerOrderId;
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = options.Actor;
            command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
                options.SourceVersion;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task RetryAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperSubmissionStatus.RetryPending,
            reasons,
            availableAtUtc,
            null,
            cancellationToken);

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperSubmissionStatus.Rejected,
            reasons,
            null,
            null,
            cancellationToken);

    public Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperSubmissionStatus.Expired,
            new[] { reason },
            null,
            null,
            cancellationToken);

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperSubmissionStatus.Failed,
            new[] { "PAPER_SUBMISSION_FAILED" },
            null,
            error,
            cancellationToken);

    private async Task UpdateAsync(
        long workItemId,
        string status,
        IReadOnlyCollection<string> reasons,
        DateTimeOffset? availableAtUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [execution].[paper_submission_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = @reasons_json,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_submission_work_item_id] = @work_item_id;
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@next_attempt_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(reasons);
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> Validate(
        AutomaticPaperSubmissionCandidate candidate)
    {
        var reasons = new List<string>();
        if (candidate.OrderId <= 0 || candidate.ExecutionCommandId <= 0)
            reasons.Add("PERSISTED_ORDER_IDENTITY_REQUIRED");
        if (candidate.OrderUid == Guid.Empty || candidate.ExecutionCommandUid == Guid.Empty)
            reasons.Add("ORDER_LINEAGE_REQUIRED");
        if (!Guid.TryParse(candidate.CorrelationId, out _))
            reasons.Add("CORRELATION_ID_INVALID");
        if (candidate.ValidUntilUtc <= DateTimeOffset.UtcNow)
            reasons.Add("EXECUTION_COMMAND_EXPIRED");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
