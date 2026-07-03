using Microsoft.Data.SqlClient;

namespace ThesisPulse.Signal.Service;

public sealed class SqlServerOptionChainWorkQueue : IOptionChainWorkQueue
{
    private readonly SqlServerOptionChainWorkQueueOptions _options;

    public SqlServerOptionChainWorkQueue(SqlServerOptionChainWorkQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<bool> EnqueueAsync(OptionChainWorkItem workItem, CancellationToken cancellationToken = default)
    {
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM intelligence.option_chain_work_queue WITH (UPDLOCK, HOLDLOCK) WHERE work_uid = @work_uid)
            BEGIN
                INSERT INTO intelligence.option_chain_work_queue
                (work_uid, snapshot_uid, instrument_key, workflow_cutoff_utc, engine_version, policy_version,
                 status, attempt_count, available_at_utc, lease_owner, lease_expires_at_utc, terminal_reason,
                 created_at_utc, updated_at_utc)
                VALUES
                (@work_uid, @snapshot_uid, @instrument_key, @workflow_cutoff_utc, @engine_version, @policy_version,
                 N'PENDING', 0, @available_at_utc, NULL, NULL, NULL, @created_at_utc, @updated_at_utc);
                SELECT CAST(1 AS bit);
            END
            ELSE SELECT CAST(0 AS bit);
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        AddWorkParameters(command, workItem);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<OptionChainWorkLease?> TryLeaseAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        const string sql = """
            ;WITH candidate AS
            (
                SELECT TOP (1) *
                FROM intelligence.option_chain_work_queue WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE available_at_utc <= @now_utc
                  AND (status = N'PENDING' OR (status = N'LEASED' AND lease_expires_at_utc <= @now_utc))
                ORDER BY available_at_utc, created_at_utc, work_uid
            )
            UPDATE candidate
            SET status = N'LEASED', attempt_count = attempt_count + 1,
                lease_owner = @lease_owner, lease_expires_at_utc = @lease_expires_at_utc,
                terminal_reason = NULL, updated_at_utc = @now_utc
            OUTPUT inserted.work_uid, inserted.snapshot_uid, inserted.instrument_key,
                   inserted.workflow_cutoff_utc, inserted.engine_version, inserted.policy_version,
                   inserted.status, inserted.attempt_count, inserted.available_at_utc,
                   inserted.lease_owner, inserted.lease_expires_at_utc, inserted.terminal_reason,
                   inserted.created_at_utc, inserted.updated_at_utc;
            """;

        var expiresAt = nowUtc.Add(leaseDuration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@now_utc", nowUtc.UtcDateTime);
        command.Parameters.AddWithValue("@lease_owner", leaseOwner);
        command.Parameters.AddWithValue("@lease_expires_at_utc", expiresAt.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var item = ReadWorkItem(reader);
        return new OptionChainWorkLease(item, leaseOwner, expiresAt);
    }

    public Task<bool> CompleteAsync(
        Guid workUid,
        string leaseOwner,
        OptionChainWorkStatus terminalStatus,
        string? terminalReason,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        UpdateLeaseAsync(workUid, leaseOwner, terminalStatus, terminalReason, completedAtUtc, null, cancellationToken);

    public Task<bool> RetryAsync(
        Guid workUid,
        string leaseOwner,
        DateTimeOffset availableAtUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        UpdateLeaseAsync(workUid, leaseOwner, OptionChainWorkStatus.Pending, reason, availableAtUtc, availableAtUtc, cancellationToken);

    public async Task<OptionChainWorkerMetrics> GetMetricsAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN status = N'PENDING' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'LEASED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'COMPLETED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'DUPLICATE' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'REJECTED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'FAILED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = N'PENDING' AND attempt_count > 0 THEN 1 ELSE 0 END),
                MIN(CASE WHEN status = N'PENDING' THEN created_at_utc END)
            FROM intelligence.option_chain_work_queue;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new OptionChainWorkerMetrics(
            ReadLong(reader, 0), ReadLong(reader, 1), ReadLong(reader, 2), ReadLong(reader, 3),
            ReadLong(reader, 4), ReadLong(reader, 5), ReadLong(reader, 6),
            reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
            observedAtUtc);
    }

    private async Task<bool> UpdateLeaseAsync(
        Guid workUid,
        string leaseOwner,
        OptionChainWorkStatus status,
        string? reason,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? availableAtUtc,
        CancellationToken cancellationToken)
    {
        var sql = """
            UPDATE intelligence.option_chain_work_queue
            SET status = @status,
                available_at_utc = COALESCE(@available_at_utc, available_at_utc),
                lease_owner = NULL,
                lease_expires_at_utc = NULL,
                terminal_reason = @terminal_reason,
                updated_at_utc = @updated_at_utc
            WHERE work_uid = @work_uid AND status = N'LEASED' AND lease_owner = @lease_owner;
            SELECT @@ROWCOUNT;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@status", ToDatabase(status));
        command.Parameters.AddWithValue("@available_at_utc", availableAtUtc is null ? DBNull.Value : availableAtUtc.Value.UtcDateTime);
        command.Parameters.AddWithValue("@terminal_reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@updated_at_utc", updatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@work_uid", workUid);
        command.Parameters.AddWithValue("@lease_owner", leaseOwner);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) => new(sql, connection)
    {
        CommandTimeout = _options.CommandTimeoutSeconds,
    };

    private static void AddWorkParameters(SqlCommand command, OptionChainWorkItem item)
    {
        command.Parameters.AddWithValue("@work_uid", item.WorkUid);
        command.Parameters.AddWithValue("@snapshot_uid", item.SnapshotUid);
        command.Parameters.AddWithValue("@instrument_key", item.InstrumentKey);
        command.Parameters.AddWithValue("@workflow_cutoff_utc", item.WorkflowCutoffUtc.UtcDateTime);
        command.Parameters.AddWithValue("@engine_version", item.EngineVersion);
        command.Parameters.AddWithValue("@policy_version", item.PolicyVersion);
        command.Parameters.AddWithValue("@available_at_utc", item.AvailableAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@created_at_utc", item.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@updated_at_utc", item.UpdatedAtUtc.UtcDateTime);
    }

    private static OptionChainWorkItem ReadWorkItem(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
        new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
        reader.GetString(4), reader.GetString(5), ParseStatus(reader.GetString(6)), reader.GetInt32(7),
        new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
        new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero));

    private static long ReadLong(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));

    private static string ToDatabase(OptionChainWorkStatus status) => status.ToString().ToUpperInvariant();

    private static OptionChainWorkStatus ParseStatus(string value) => Enum.Parse<OptionChainWorkStatus>(value, true);
}
