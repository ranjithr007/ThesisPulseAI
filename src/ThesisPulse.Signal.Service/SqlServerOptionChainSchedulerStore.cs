using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainSchedulerLease(
    string JobName,
    string OwnerInstance,
    Guid LeaseUid,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset HeartbeatAtUtc);

public sealed record OptionChainSchedulerRun(
    Guid RunUid,
    string JobName,
    string OwnerInstance,
    string Outcome,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Detail,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public interface IOptionChainSchedulerStore
{
    ValueTask<OptionChainSchedulerLease?> TryAcquireAsync(
        string jobName,
        string ownerInstance,
        TimeSpan duration,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<bool> RenewAsync(
        OptionChainSchedulerLease lease,
        TimeSpan duration,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(OptionChainSchedulerLease lease, CancellationToken cancellationToken);
    ValueTask RecordRunAsync(OptionChainSchedulerRun run, CancellationToken cancellationToken);
}

public sealed class SqlServerOptionChainSchedulerStore(
    IConfiguration configuration,
    OptionChainSqlRuntimeOptions runtime) : IOptionChainSchedulerStore
{
    private string DatabaseConnection =>
        configuration["OptionChainSqlOperations:DatabaseConnection"]
        ?? throw new InvalidOperationException("Option-chain SQL database configuration is missing.");

    public async ValueTask<OptionChainSchedulerLease?> TryAcquireAsync(
        string jobName,
        string ownerInstance,
        TimeSpan duration,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var leaseUid = Guid.NewGuid();
        var expiresAtUtc = observedAtUtc.Add(duration);
        const string sql = """
            MERGE intelligence.option_chain_scheduler_leases WITH (HOLDLOCK) AS target
            USING (SELECT @job_name AS job_name) AS source
                ON target.job_name = source.job_name
            WHEN MATCHED AND target.expires_at_utc <= @observed_at_utc THEN
                UPDATE SET owner_instance = @owner_instance,
                           lease_uid = @lease_uid,
                           acquired_at_utc = @observed_at_utc,
                           expires_at_utc = @expires_at_utc,
                           heartbeat_at_utc = @observed_at_utc
            WHEN NOT MATCHED THEN
                INSERT (job_name, owner_instance, lease_uid, acquired_at_utc, expires_at_utc, heartbeat_at_utc)
                VALUES (@job_name, @owner_instance, @lease_uid, @observed_at_utc, @expires_at_utc, @observed_at_utc)
            OUTPUT inserted.job_name, inserted.owner_instance, inserted.lease_uid,
                   inserted.acquired_at_utc, inserted.expires_at_utc, inserted.heartbeat_at_utc;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 64).Value = jobName;
        command.Parameters.Add("@owner_instance", SqlDbType.NVarChar, 128).Value = ownerInstance;
        command.Parameters.Add("@lease_uid", SqlDbType.UniqueIdentifier).Value = leaseUid;
        command.Parameters.Add("@observed_at_utc", SqlDbType.DateTime2).Value = observedAtUtc.UtcDateTime;
        command.Parameters.Add("@expires_at_utc", SqlDbType.DateTime2).Value = expiresAtUtc.UtcDateTime;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadLease(reader);
    }

    public async ValueTask<bool> RenewAsync(
        OptionChainSchedulerLease lease,
        TimeSpan duration,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE intelligence.option_chain_scheduler_leases
            SET heartbeat_at_utc = @observed_at_utc,
                expires_at_utc = @expires_at_utc
            WHERE job_name = @job_name
              AND owner_instance = @owner_instance
              AND lease_uid = @lease_uid
              AND expires_at_utc > @observed_at_utc;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 64).Value = lease.JobName;
        command.Parameters.Add("@owner_instance", SqlDbType.NVarChar, 128).Value = lease.OwnerInstance;
        command.Parameters.Add("@lease_uid", SqlDbType.UniqueIdentifier).Value = lease.LeaseUid;
        command.Parameters.Add("@observed_at_utc", SqlDbType.DateTime2).Value = observedAtUtc.UtcDateTime;
        command.Parameters.Add("@expires_at_utc", SqlDbType.DateTime2).Value = observedAtUtc.Add(duration).UtcDateTime;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask ReleaseAsync(OptionChainSchedulerLease lease, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM intelligence.option_chain_scheduler_leases WHERE job_name = @job_name AND owner_instance = @owner_instance AND lease_uid = @lease_uid";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 64).Value = lease.JobName;
        command.Parameters.Add("@owner_instance", SqlDbType.NVarChar, 128).Value = lease.OwnerInstance;
        command.Parameters.Add("@lease_uid", SqlDbType.UniqueIdentifier).Value = lease.LeaseUid;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask RecordRunAsync(OptionChainSchedulerRun run, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO intelligence.option_chain_scheduler_runs
            (run_uid, job_name, owner_instance, outcome, started_at_utc, completed_at_utc,
             detail, selection_authority, execution_authority)
            VALUES
            (@run_uid, @job_name, @owner_instance, @outcome, @started_at_utc, @completed_at_utc,
             @detail, @selection_authority, @execution_authority);
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@run_uid", SqlDbType.UniqueIdentifier).Value = run.RunUid;
        command.Parameters.Add("@job_name", SqlDbType.NVarChar, 64).Value = run.JobName;
        command.Parameters.Add("@owner_instance", SqlDbType.NVarChar, 128).Value = run.OwnerInstance;
        command.Parameters.Add("@outcome", SqlDbType.NVarChar, 32).Value = run.Outcome;
        command.Parameters.Add("@started_at_utc", SqlDbType.DateTime2).Value = run.StartedAtUtc.UtcDateTime;
        command.Parameters.Add("@completed_at_utc", SqlDbType.DateTime2).Value = run.CompletedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 1024).Value = run.Detail ?? (object)DBNull.Value;
        command.Parameters.Add("@selection_authority", SqlDbType.Bit).Value = false;
        command.Parameters.Add("@execution_authority", SqlDbType.Bit).Value = false;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static OptionChainSchedulerLease ReadLease(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetGuid(2),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
}
