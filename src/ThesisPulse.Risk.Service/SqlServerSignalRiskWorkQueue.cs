using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerSignalRiskWorkQueue(
    SignalRiskPersistenceOptions persistenceOptions) : ISignalRiskWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SignalRiskEnqueueResult> EnqueueAsync(
        SignalRiskEvaluationIntakeV1 intake,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF EXISTS
            (
                SELECT 1
                FROM [risk].[signal_risk_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [message_uid] = @message_uid
            )
            BEGIN
                SELECT CAST(0 AS bit);
                RETURN;
            END;

            INSERT INTO [risk].[signal_risk_work_items]
            (
                [message_uid], [signal_uid], [intake_json], [status], [available_at_utc]
            )
            VALUES
            (
                @message_uid, @signal_uid, @intake_json, 'PENDING', SYSUTCDATETIME()
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
            command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value = intake.MessageUid;
            command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = intake.Signal.SignalUid;
            command.Parameters.Add("@intake_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(intake, JsonOptions);
            var created = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new SignalRiskEnqueueResult(
                created ? "ENQUEUED" : "DUPLICATE",
                intake.MessageUid,
                intake.Signal.SignalUid);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<SignalRiskWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [risk].[signal_risk_work_items] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([status] IN ('PENDING','RETRY_PENDING') AND [available_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([status] = 'LEASED' AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [available_at_utc], [signal_risk_work_item_id]
            )
            UPDATE ready
            SET [status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[signal_risk_work_item_id], INSERTED.[message_uid],
                   INSERTED.[signal_uid], INSERTED.[intake_json], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.VarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;

        var items = new List<SignalRiskWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var intake = JsonSerializer.Deserialize<SignalRiskEvaluationIntakeV1>(
                reader.GetString(3), JsonOptions)
                ?? throw new InvalidOperationException("Risk work item intake could not be deserialized.");
            items.Add(new SignalRiskWorkItem(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                intake,
                reader.GetInt32(4)));
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
            UPDATE [risk].[signal_risk_work_items]
            SET [status] = @status,
                [available_at_utc] = COALESCE(@available_at_utc, [available_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [signal_risk_work_item_id] = @work_item_id;
            """;
        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@available_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
