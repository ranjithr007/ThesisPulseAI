using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public sealed class SqlServerAutomaticPaperExecutionCandidateStore(
    SqlServerPaperExecutionLedgerOptions options) : IAutomaticPaperExecutionCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<AutomaticPaperExecutionCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                tp.[trade_plan_id], tp.[message_uid], tp.[raw_contract_json]
            FROM [risk].[trade_plans] tp WITH (READPAST)
            LEFT JOIN [risk].[signal_risk_evaluations] sre
                ON sre.[signal_risk_evaluation_id] = tp.[signal_risk_evaluation_id]
            LEFT JOIN [risk].[risk_decisions] rd
                ON rd.[risk_decision_id] = tp.[risk_decision_id]
            WHERE tp.[initial_status] = 'READY'
              AND tp.[environment] = 'PAPER'
              AND tp.[is_current] = 1
              AND tp.[valid_until_utc] > @as_of_utc
              AND ISJSON(tp.[raw_contract_json]) = 1
              AND
              (
                  (tp.[signal_risk_evaluation_id] IS NOT NULL
                      AND sre.[current_status] = 'RISK_APPROVED'
                      AND sre.[risk_decision_uid] IS NOT NULL)
                  OR
                  (tp.[risk_decision_id] IS NOT NULL
                      AND rd.[risk_decision_uid] IS NOT NULL)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [execution].[paper_execution_work_items] w
                  WHERE w.[trade_plan_id] = tp.[trade_plan_id]
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [execution].[execution_commands] ec
                  WHERE ec.[trade_plan_id] = tp.[trade_plan_id]
                    AND ec.[environment] = 'PAPER'
                    AND ec.[command_type] = 'PLACE'
              )
            ORDER BY tp.[created_at_utc], tp.[trade_plan_id];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;

        var candidates = new List<AutomaticPaperExecutionCandidate>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var plan = JsonSerializer.Deserialize<TradePlanV1>(reader.GetString(2), JsonOptions)
                ?? throw new InvalidOperationException(
                    "Authoritative READY Trade Plan could not be deserialized.");
            candidates.Add(new AutomaticPaperExecutionCandidate(
                reader.GetInt64(0),
                reader.GetGuid(1),
                plan));
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
}

public sealed class SqlServerAutomaticPaperExecutionWorkQueue(
    SqlServerPaperExecutionLedgerOptions options) : IAutomaticPaperExecutionWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AutomaticPaperExecutionEnqueueResult> EnqueueAsync(
        AutomaticPaperExecutionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var plan = candidate.TradePlan;
        var requestUid = AutomaticPaperExecutionIdentity.RequestUid(plan.TradePlanUid);
        var idempotencyKey = AutomaticPaperExecutionIdentity.IdempotencyKey(plan.TradePlanUid);
        var reasons = Validate(candidate);
        if (reasons.Count > 0)
        {
            return new AutomaticPaperExecutionEnqueueResult(
                AutomaticPaperExecutionStatus.Rejected,
                plan.TradePlanUid,
                requestUid,
                reasons);
        }

        const string sql = """
            DECLARE @resolved_trade_plan_id bigint =
            (
                SELECT [trade_plan_id]
                FROM [risk].[trade_plans] WITH (UPDLOCK, HOLDLOCK)
                WHERE [trade_plan_id] = @trade_plan_id
                  AND [trade_plan_uid] = @trade_plan_uid
                  AND [message_uid] = @source_message_uid
                  AND [initial_status] = 'READY'
                  AND [environment] = 'PAPER'
                  AND [is_current] = 1
                  AND [valid_until_utc] > SYSUTCDATETIME()
            );

            IF @resolved_trade_plan_id IS NULL
            BEGIN
                SELECT CAST(-1 AS int);
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [execution].[execution_commands] WITH (UPDLOCK, HOLDLOCK)
                WHERE [trade_plan_id] = @resolved_trade_plan_id
                  AND [environment] = 'PAPER'
                  AND [command_type] = 'PLACE'
            )
            BEGIN
                SELECT CAST(2 AS int);
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [execution].[paper_execution_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [trade_plan_id] = @resolved_trade_plan_id
                   OR [trade_plan_uid] = @trade_plan_uid
                   OR [request_uid] = @request_uid
                   OR [idempotency_key] = @idempotency_key
            )
            BEGIN
                SELECT CAST(0 AS int);
                RETURN;
            END;

            INSERT INTO [execution].[paper_execution_work_items]
            (
                [trade_plan_id], [trade_plan_uid], [source_message_uid], [request_uid],
                [correlation_id], [idempotency_key], [payload_json], [current_status],
                [next_attempt_at_utc]
            )
            VALUES
            (
                @resolved_trade_plan_id, @trade_plan_uid, @source_message_uid, @request_uid,
                @correlation_id, @idempotency_key, @payload_json, 'PENDING', SYSUTCDATETIME()
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
            command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = candidate.TradePlanId;
            command.Parameters.Add("@trade_plan_uid", SqlDbType.UniqueIdentifier).Value = plan.TradePlanUid;
            command.Parameters.Add("@source_message_uid", SqlDbType.UniqueIdentifier).Value = candidate.SourceMessageUid;
            command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = requestUid;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(plan.CorrelationId);
            command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value = idempotencyKey;
            command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(plan, JsonOptions);
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return outcome switch
            {
                1 => new AutomaticPaperExecutionEnqueueResult(
                    "ENQUEUED", plan.TradePlanUid, requestUid, Array.Empty<string>()),
                0 => new AutomaticPaperExecutionEnqueueResult(
                    "DUPLICATE", plan.TradePlanUid, requestUid, Array.Empty<string>()),
                2 => new AutomaticPaperExecutionEnqueueResult(
                    "ALREADY_AUTHORIZED", plan.TradePlanUid, requestUid, Array.Empty<string>()),
                _ => new AutomaticPaperExecutionEnqueueResult(
                    AutomaticPaperExecutionStatus.Expired,
                    plan.TradePlanUid,
                    requestUid,
                    new[] { "AUTHORITATIVE_PLAN_READY_NOT_FOUND" }),
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPaperExecutionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [execution].[paper_execution_work_items]
                    WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING','RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [paper_execution_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[paper_execution_work_item_id], INSERTED.[trade_plan_id],
                   INSERTED.[source_message_uid], INSERTED.[request_uid],
                   INSERTED.[correlation_id], INSERTED.[idempotency_key],
                   INSERTED.[payload_json], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value =
            (int)leaseDuration.TotalSeconds;

        var items = new List<AutomaticPaperExecutionWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var plan = JsonSerializer.Deserialize<TradePlanV1>(reader.GetString(6), JsonOptions)
                ?? throw new InvalidOperationException(
                    "Automatic PAPER execution payload could not be deserialized.");
            items.Add(new AutomaticPaperExecutionWorkItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4).ToString("D"),
                reader.GetString(5),
                plan,
                reader.GetInt32(7)));
        }
        return items;
    }

    public Task AuthorizeAsync(
        long workItemId,
        Guid executionCommandUid,
        Guid orderUid,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperExecutionStatus.Authorized,
            null,
            null,
            executionCommandUid,
            orderUid,
            "[]",
            cancellationToken);

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperExecutionStatus.RetryPending,
            error,
            availableAtUtc,
            null,
            null,
            "[]",
            cancellationToken);

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperExecutionStatus.Rejected,
            null,
            null,
            null,
            null,
            JsonSerializer.Serialize(reasons, JsonOptions),
            cancellationToken);

    public Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperExecutionStatus.Expired,
            reason,
            null,
            null,
            null,
            JsonSerializer.Serialize(new[] { reason }, JsonOptions),
            cancellationToken);

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperExecutionStatus.Failed,
            error,
            null,
            null,
            null,
            "[]",
            cancellationToken);

    private async Task UpdateAsync(
        long workItemId,
        string status,
        string? error,
        DateTimeOffset? availableAtUtc,
        Guid? executionCommandUid,
        Guid? orderUid,
        string reasonsJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [execution].[paper_execution_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [execution_command_uid] = @execution_command_uid,
                [order_uid] = @order_uid,
                [rejection_reasons_json] = @rejection_reasons_json,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [paper_execution_work_item_id] = @work_item_id;
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@next_attempt_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@execution_command_uid", SqlDbType.UniqueIdentifier).Value =
            executionCommandUid.HasValue ? executionCommandUid.Value : DBNull.Value;
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value =
            orderUid.HasValue ? orderUid.Value : DBNull.Value;
        command.Parameters.Add("@rejection_reasons_json", SqlDbType.NVarChar, -1).Value =
            reasonsJson;
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> Validate(
        AutomaticPaperExecutionCandidate candidate)
    {
        var plan = candidate.TradePlan;
        var reasons = new List<string>();
        if (candidate.TradePlanId <= 0 || candidate.SourceMessageUid == Guid.Empty)
            reasons.Add("PERSISTED_TRADE_PLAN_IDENTITY_REQUIRED");
        if (plan.TradePlanUid == Guid.Empty ||
            plan.RiskDecisionUid == Guid.Empty ||
            plan.ThesisUid == Guid.Empty ||
            plan.SignalUid == Guid.Empty)
            reasons.Add("TRADE_PLAN_LINEAGE_REQUIRED");
        if (!Guid.TryParse(plan.CorrelationId, out _))
            reasons.Add("CORRELATION_ID_INVALID");
        if (!string.Equals(plan.Status, TradePlanContractV1.Ready, StringComparison.Ordinal))
            reasons.Add("PLAN_READY_REQUIRED");
        if (!string.Equals(
                plan.Environment,
                ExecutionCommandContractV1.PaperEnvironment,
                StringComparison.OrdinalIgnoreCase))
            reasons.Add("PAPER_ENVIRONMENT_REQUIRED");
        if (plan.ExecutionAuthorized)
            reasons.Add("UPSTREAM_EXECUTION_AUTHORITY_FORBIDDEN");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
}
