using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerSignalRiskEvaluationStore : ISignalRiskEvaluationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SignalRiskPersistenceOptions _options;

    public SqlServerSignalRiskEvaluationStore(SignalRiskPersistenceOptions options)
    {
        options.Validate();
        _options = options;
    }

    public StoredRiskEvaluation? Get(Guid commandUid)
    {
        using var connection = new SqlConnection(_options.ConnectionString);
        connection.Open();
        return FindExisting(connection, null, commandUid, lockRow: false);
    }

    public StoredRiskEvaluation Save(
        SignalRiskEvaluationCommandV1 command,
        StoredRiskEvaluation evaluation)
    {
        using var connection = new SqlConnection(_options.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        try
        {
            var existing = FindExisting(connection, transaction, command.CommandUid, lockRow: true);
            if (existing is not null)
            {
                transaction.Commit();
                return existing;
            }

            var signalId = ResolveSignalId(connection, transaction, command.SignalUid);
            var decisionJson = JsonSerializer.Serialize(evaluation.Decision, JsonOptions);
            var evaluationId = InsertEvaluation(
                connection,
                transaction,
                command,
                evaluation,
                signalId,
                decisionJson);

            InsertStatusEvent(
                connection,
                transaction,
                evaluationId,
                1,
                RiskStatusTransitionMatrix.NotEvaluated,
                SignalRiskEvaluationContractV1.RiskEvaluating,
                command);
            InsertStatusEvent(
                connection,
                transaction,
                evaluationId,
                2,
                SignalRiskEvaluationContractV1.RiskEvaluating,
                evaluation.CurrentStatus,
                command);

            transaction.Commit();
            return evaluation;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static StoredRiskEvaluation? FindExisting(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid commandUid,
        bool lockRow)
    {
        var lockHint = lockRow ? "WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT [command_uid], s.[signal_uid], [risk_decision_uid],
                   [decision_snapshot_json], [current_status]
            FROM [risk].[signal_risk_evaluations] e {lockHint}
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = e.[signal_id]
            WHERE e.[command_uid] = @command_uid;
            """;

        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value = commandUid;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var decisionJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (string.IsNullOrWhiteSpace(decisionJson))
            throw new InvalidOperationException("Stored terminal risk evaluation is missing its decision snapshot.");

        var decision = JsonSerializer.Deserialize<RiskDecisionV1>(decisionJson, JsonOptions)
            ?? throw new InvalidOperationException("Stored risk decision snapshot could not be deserialized.");
        var status = reader.GetString(4);

        return new StoredRiskEvaluation(
            reader.GetGuid(0),
            reader.GetGuid(1),
            decision,
            status,
            new[]
            {
                RiskStatusTransitionMatrix.NotEvaluated,
                SignalRiskEvaluationContractV1.RiskEvaluating,
                status,
            });
    }

    private static long ResolveSignalId(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid signalUid)
    {
        const string sql = """
            SELECT [signal_id]
            FROM [intelligence].[signals] WITH (UPDLOCK, HOLDLOCK)
            WHERE [signal_uid] = @signal_uid;
            """;
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = signalUid;
        var value = command.ExecuteScalar();
        return value is long id
            ? id
            : throw new InvalidOperationException($"Canonical signal '{signalUid}' was not found.");
    }

    private static long InsertEvaluation(
        SqlConnection connection,
        SqlTransaction transaction,
        SignalRiskEvaluationCommandV1 source,
        StoredRiskEvaluation evaluation,
        long signalId,
        string decisionJson)
    {
        const string sql = """
            INSERT INTO [risk].[signal_risk_evaluations]
            (
                [command_uid], [request_uid], [source_message_uid], [signal_id],
                [risk_decision_uid], [decision_snapshot_json], [contract_version],
                [risk_policy_version], [current_status], [attempt_count],
                [correlation_id], [causation_id], [updated_at_utc]
            )
            OUTPUT INSERTED.[signal_risk_evaluation_id]
            VALUES
            (
                @command_uid, @request_uid, @source_message_uid, @signal_id,
                @risk_decision_uid, @decision_json, @contract_version,
                @risk_policy_version, @current_status, 1,
                @correlation_id, @causation_id, @updated_at_utc
            );
            """;
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@command_uid", SqlDbType.UniqueIdentifier).Value = source.CommandUid;
        command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = source.RequestUid;
        command.Parameters.Add("@source_message_uid", SqlDbType.UniqueIdentifier).Value = source.SourceMessageUid;
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
        command.Parameters.Add("@risk_decision_uid", SqlDbType.UniqueIdentifier).Value = evaluation.Decision.RiskDecisionUid;
        command.Parameters.Add("@decision_json", SqlDbType.NVarChar, -1).Value = decisionJson;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value = source.ContractVersion;
        command.Parameters.Add("@risk_policy_version", SqlDbType.VarChar, 100).Value = source.Request.RiskPolicyVersion;
        command.Parameters.Add("@current_status", SqlDbType.VarChar, 30).Value = evaluation.CurrentStatus;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(source.CorrelationId);
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            source.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        command.Parameters.Add("@updated_at_utc", SqlDbType.DateTime2).Value = evaluation.Decision.EvaluatedAtUtc.UtcDateTime;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertStatusEvent(
        SqlConnection connection,
        SqlTransaction transaction,
        long evaluationId,
        int sequence,
        string fromStatus,
        string toStatus,
        SignalRiskEvaluationCommandV1 source)
    {
        RiskStatusTransitionMatrix.EnsureAllowed(fromStatus, toStatus);
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
                @from_status, @to_status, @occurred_at_utc,
                @correlation_id, @causation_id
            );
            """;
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@transition_uid", SqlDbType.UniqueIdentifier).Value =
            DeterministicGuidV1.Create(source.CommandUid, $"risk-status|{sequence}|{toStatus}");
        command.Parameters.Add("@evaluation_id", SqlDbType.BigInt).Value = evaluationId;
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence;
        command.Parameters.Add("@from_status", SqlDbType.VarChar, 30).Value = fromStatus;
        command.Parameters.Add("@to_status", SqlDbType.VarChar, 30).Value = toStatus;
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value = source.CreatedAtUtc.UtcDateTime;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = Guid.Parse(source.CorrelationId);
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            source.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        command.ExecuteNonQuery();
    }
}
