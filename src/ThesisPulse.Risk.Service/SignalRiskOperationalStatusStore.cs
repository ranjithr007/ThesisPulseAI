using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public interface ISignalRiskOperationalStatusStore
{
    Task RecordAsync(
        SignalRiskEvaluationIntakeV1 intake,
        string status,
        string reason,
        CancellationToken cancellationToken);
}

public sealed class SqlServerSignalRiskOperationalStatusStore(
    SignalRiskPersistenceOptions options) : ISignalRiskOperationalStatusStore
{
    public async Task RecordAsync(
        SignalRiskEvaluationIntakeV1 intake,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        var requestUid = DeterministicGuidV1.Create(
            intake.Signal.SignalUid,
            $"risk-request.v1|{intake.RiskPolicyVersion}|{intake.Lineage.FusionPolicyVersion}");
        var commandUid = DeterministicGuidV1.Create(
            requestUid,
            $"{SignalRiskEvaluationContractV1.CommandType}|{intake.MessageUid:N}");

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var signalId = await ResolveSignalIdAsync(connection, transaction, intake.Signal.SignalUid, cancellationToken);
            var current = await GetCurrentAsync(connection, transaction, commandUid, cancellationToken);
            if (current is null)
            {
                RiskStatusTransitionMatrix.EnsureAllowed(RiskStatusTransitionMatrix.NotEvaluated, status);
                var evaluationId = await InsertEvaluationAsync(
                    connection,
                    transaction,
                    intake,
                    commandUid,
                    requestUid,
                    signalId,
                    status,
                    cancellationToken);
                await InsertEventAsync(
                    connection,
                    transaction,
                    evaluationId,
                    1,
                    RiskStatusTransitionMatrix.NotEvaluated,
                    status,
                    intake,
                    reason,
                    cancellationToken);
            }
            else if (!string.Equals(current.Value.Status, status, StringComparison.Ordinal))
            {
                RiskStatusTransitionMatrix.EnsureAllowed(current.Value.Status, status);
                await UpdateStatusAsync(
                    connection,
                    transaction,
                    current.Value.EvaluationId,
                    status,
                    cancellationToken);
                await InsertEventAsync(
                    connection,
                    transaction,
                    current.Value.EvaluationId,
                    current.Value.NextSequence,
                    current.Value.Status,
                    status,
                    intake,
                    reason,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<long> ResolveSignalIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid signalUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [signal_id]
            FROM [intelligence].[signals] WITH (UPDLOCK, HOLDLOCK)
            WHERE [signal_uid] = @signal_uid;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = signalUid;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long id
            ? id
            : throw new InvalidOperationException($"Canonical signal '{signalUid}' was not found.");
    }

    private static async Task<(long EvaluationId, string Status, int NextSequence)?> GetCurrentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid commandUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.[signal_risk_evaluation_id], e.[current_status],
                   ISNULL(MAX(se.[event_sequence]), 0) + 1
            FROM [risk].[signal_risk_evaluations] e WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN [risk].[signal_risk_status_events] se
                ON se.[signal_risk_evaluation_id] = e.[signal_risk_evaluation_id]
            WHERE e.[command_uid] = @command_uid
            GROUP BY e.[signal_risk_evaluation_id], e.[current_status];
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value = commandUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2))
            : null;
    }

    private static async Task<long> InsertEvaluationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SignalRiskEvaluationIntakeV1 intake,
        Guid commandUid,
        Guid requestUid,
        long signalId,
        string status,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[signal_risk_evaluations]
            (
                [command_uid], [request_uid], [source_message_uid], [signal_id],
                [contract_version], [risk_policy_version], [current_status],
                [attempt_count], [correlation_id], [causation_id], [updated_at_utc]
            )
            OUTPUT INSERTED.[signal_risk_evaluation_id]
            VALUES
            (
                @command_uid, @request_uid, @source_message_uid, @signal_id,
                @contract_version, @risk_policy_version, @current_status,
                1, @correlation_id, @causation_id, SYSUTCDATETIME()
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value = commandUid;
        command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = requestUid;
        command.Parameters.Add("@source_message_uid", SqlDbType.UniqueIdentifier).Value = intake.MessageUid;
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value = SignalRiskEvaluationContractV1.ContractVersion;
        command.Parameters.Add("@risk_policy_version", SqlDbType.VarChar, 100).Value = intake.RiskPolicyVersion;
        command.Parameters.Add("@current_status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(intake.CorrelationId);
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            intake.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateStatusAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long evaluationId,
        string status,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[signal_risk_evaluations]
            SET [current_status] = @status,
                [attempt_count] = [attempt_count] + 1,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [signal_risk_evaluation_id] = @evaluation_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@evaluation_id", SqlDbType.BigInt).Value = evaluationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long evaluationId,
        int sequence,
        string fromStatus,
        string toStatus,
        SignalRiskEvaluationIntakeV1 intake,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[signal_risk_status_events]
            (
                [transition_uid], [signal_risk_evaluation_id], [event_sequence],
                [from_status], [to_status], [occurred_at_utc],
                [correlation_id], [causation_id]
            )
            VALUES
            (
                @transition_uid, @evaluation_id, @sequence,
                @from_status, @to_status, SYSUTCDATETIME(),
                @correlation_id, @causation_id
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@transition_uid", SqlDbType.UniqueIdentifier).Value =
            DeterministicGuidV1.Create(intake.MessageUid, $"risk-operation|{sequence}|{toStatus}|{reason}");
        command.Parameters.Add("@evaluation_id", SqlDbType.BigInt).Value = evaluationId;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence;
        command.Parameters.Add("@from_status", SqlDbType.VarChar, 30).Value = fromStatus;
        command.Parameters.Add("@to_status", SqlDbType.VarChar, 30).Value = toStatus;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(intake.CorrelationId);
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            intake.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
