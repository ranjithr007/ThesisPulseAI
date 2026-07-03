using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Signal.Service;

public sealed class SqlServerOptionChainRolloutAuditStore(
    IConfiguration configuration,
    OptionChainSqlRuntimeOptions runtime) : IOptionChainRolloutAuditStore
{
    private string DatabaseConnection =>
        configuration["OptionChainSqlOperations:DatabaseConnection"]
        ?? throw new InvalidOperationException("Option-chain SQL database configuration is missing.");

    public async ValueTask<OptionChainRolloutAuditRecord?> GetLatestAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP (1) audit_uid, correlation_uid, command_key, actor, action_code, previous_mode, new_mode, previous_version, new_version, reason, source_service, observed_at_utc, selection_authority, execution_authority FROM intelligence.option_chain_rollout_audit ORDER BY new_version DESC";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async ValueTask<OptionChainRolloutAuditRecord?> FindByCommandKeyAsync(string commandKey, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP (1) audit_uid, correlation_uid, command_key, actor, action_code, previous_mode, new_mode, previous_version, new_version, reason, source_service, observed_at_utc, selection_authority, execution_authority FROM intelligence.option_chain_rollout_audit WHERE command_key = @command_key";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@command_key", SqlDbType.NVarChar, 128).Value = commandKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async ValueTask AppendAsync(OptionChainRolloutAuditRecord record, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO intelligence.option_chain_rollout_audit (audit_uid, correlation_uid, command_key, actor, action_code, previous_mode, new_mode, previous_version, new_version, reason, source_service, observed_at_utc, selection_authority, execution_authority) VALUES (@audit_uid, @correlation_uid, @command_key, @actor, @action_code, @previous_mode, @new_mode, @previous_version, @new_version, @reason, @source_service, @observed_at_utc, @selection_authority, @execution_authority)";
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction)
        {
            CommandTimeout = runtime.CommandTimeoutSeconds
        };
        Add(command, record);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("ROLLOUT_COMMAND_OR_VERSION_CONFLICT", ex);
        }
    }

    public async ValueTask<IReadOnlyCollection<OptionChainRolloutAuditRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP (@limit) audit_uid, correlation_uid, command_key, actor, action_code, previous_mode, new_mode, previous_version, new_version, reason, source_service, observed_at_utc, selection_authority, execution_authority FROM intelligence.option_chain_rollout_audit ORDER BY observed_at_utc DESC";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 500);
        var items = new List<OptionChainRolloutAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM intelligence.option_chain_rollout_audit WHERE observed_at_utc < @cutoff AND new_version < (SELECT ISNULL(MAX(new_version), 0) FROM intelligence.option_chain_rollout_audit)";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@cutoff", SqlDbType.DateTime2).Value = cutoffUtc.UtcDateTime;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(DatabaseConnection);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) => new(sql, connection)
    {
        CommandTimeout = runtime.CommandTimeoutSeconds
    };

    private static void Add(SqlCommand command, OptionChainRolloutAuditRecord value)
    {
        command.Parameters.Add("@audit_uid", SqlDbType.UniqueIdentifier).Value = value.AuditUid;
        command.Parameters.Add("@correlation_uid", SqlDbType.UniqueIdentifier).Value = value.CorrelationUid;
        command.Parameters.Add("@command_key", SqlDbType.NVarChar, 128).Value = value.CommandKey;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 128).Value = value.Actor;
        command.Parameters.Add("@action_code", SqlDbType.NVarChar, 32).Value = value.Action;
        command.Parameters.Add("@previous_mode", SqlDbType.NVarChar, 32).Value = value.PreviousMode;
        command.Parameters.Add("@new_mode", SqlDbType.NVarChar, 32).Value = value.NewMode;
        command.Parameters.Add("@previous_version", SqlDbType.BigInt).Value = value.PreviousVersion;
        command.Parameters.Add("@new_version", SqlDbType.BigInt).Value = value.NewVersion;
        command.Parameters.Add("@reason", SqlDbType.NVarChar, 512).Value = value.Reason;
        command.Parameters.Add("@source_service", SqlDbType.NVarChar, 128).Value = value.SourceService;
        command.Parameters.Add("@observed_at_utc", SqlDbType.DateTime2).Value = value.ObservedAtUtc.UtcDateTime;
        command.Parameters.Add("@selection_authority", SqlDbType.Bit).Value = value.SelectionAuthority;
        command.Parameters.Add("@execution_authority", SqlDbType.Bit).Value = value.ExecutionAuthority;
    }

    private static OptionChainRolloutAuditRecord Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetString(9),
        reader.GetString(10), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc)),
        reader.GetBoolean(12), reader.GetBoolean(13));
}
