using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerAutomaticTradePlanWorkQueue(
    SignalRiskPersistenceOptions persistenceOptions,
    IAutomaticTradePlanProjector projector) : IAutomaticTradePlanWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AutomaticTradePlanEnqueueResult> EnqueueAsync(
        AutomaticTradePlanIntakeV1 intake,
        CancellationToken cancellationToken)
    {
        var projection = projector.Project(intake);
        if (projection.Command is null)
        {
            return new AutomaticTradePlanEnqueueResult(
                AutomaticTradePlanContractV1.Rejected,
                intake.MessageUid,
                intake.RiskDecision.RiskDecisionUid,
                projection.Reasons);
        }

        if (!Guid.TryParse(intake.CorrelationId, out var correlationId))
        {
            return new AutomaticTradePlanEnqueueResult(
                AutomaticTradePlanContractV1.Rejected,
                intake.MessageUid,
                intake.RiskDecision.RiskDecisionUid,
                new[] { "CORRELATION_ID_INVALID" });
        }

        const string sql = """
            IF EXISTS
            (
                SELECT 1
                FROM [risk].[trade_plan_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [source_message_uid] = @source_message_uid
                   OR [command_uid] = @command_uid
                   OR [risk_decision_uid] = @risk_decision_uid
            )
            BEGIN
                SELECT CAST(0 AS bit);
                RETURN;
            END;

            INSERT INTO [risk].[trade_plan_work_items]
            (
                [source_message_uid], [command_uid], [request_uid], [risk_decision_uid],
                [signal_uid], [thesis_uid], [correlation_id], [causation_id],
                [payload_json], [current_status], [next_attempt_at_utc]
            )
            VALUES
            (
                @source_message_uid, @command_uid, @request_uid, @risk_decision_uid,
                @signal_uid, @thesis_uid, @correlation_id, @causation_id,
                @payload_json, 'PENDING', SYSUTCDATETIME()
            );
            SELECT CAST(1 AS bit);
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@source_message_uid", SqlDbType.UniqueIdentifier).Value = intake.MessageUid;
            command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value = projection.Command.CommandUid;
            command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = projection.Command.RequestUid;
            command.Parameters.Add("@risk_decision_uid", SqlDbType.UniqueIdentifier).Value = projection.Command.RiskDecisionUid;
            command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = projection.Command.SignalUid;
            command.Parameters.Add("@thesis_uid", SqlDbType.UniqueIdentifier).Value = projection.Command.ThesisUid;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = correlationId;
            command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
                intake.CausationMessageUid is Guid causation ? causation : DBNull.Value;
            command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(intake, JsonOptions);
            var created = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new AutomaticTradePlanEnqueueResult(
                created ? "ENQUEUED" : "DUPLICATE",
                intake.MessageUid,
                intake.RiskDecision.RiskDecisionUid,
                Array.Empty<string>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticTradePlanWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [risk].[trade_plan_work_items] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING','RETRY_PENDING') AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED' AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [trade_plan_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[trade_plan_work_item_id], INSERTED.[source_message_uid],
                   INSERTED.[command_uid], INSERTED.[risk_decision_uid],
                   INSERTED.[payload_json], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;

        var items = new List<AutomaticTradePlanWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var intake = JsonSerializer.Deserialize<AutomaticTradePlanIntakeV1>(reader.GetString(4), JsonOptions)
                ?? throw new InvalidOperationException("Trade Plan work-item payload could not be deserialized.");
            items.Add(new AutomaticTradePlanWorkItem(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                intake,
                reader.GetInt32(5)));
        }
        return items;
    }

    public Task CompleteAsync(long workItemId, CancellationToken cancellationToken) =>
        UpdateAsync(workItemId, "COMPLETED", null, null, cancellationToken);

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(workItemId, "RETRY_PENDING", error, availableAtUtc, cancellationToken);

    public Task RejectAsync(long workItemId, string reason, CancellationToken cancellationToken) =>
        UpdateAsync(workItemId, "REJECTED", reason, null, cancellationToken);

    public Task ExpireAsync(long workItemId, string reason, CancellationToken cancellationToken) =>
        UpdateAsync(workItemId, "EXPIRED", reason, null, cancellationToken);

    public Task FailAsync(long workItemId, string error, CancellationToken cancellationToken) =>
        UpdateAsync(workItemId, "FAILED", error, null, cancellationToken);

    private async Task UpdateAsync(
        long workItemId,
        string status,
        string? error,
        DateTimeOffset? availableAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[trade_plan_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [trade_plan_work_item_id] = @work_item_id;
            """;
        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@next_attempt_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
