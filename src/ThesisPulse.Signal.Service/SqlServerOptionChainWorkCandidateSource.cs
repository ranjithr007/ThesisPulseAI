using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Signal.Service;

public sealed record SqlServerOptionChainWorkCandidateSourceOptions
{
    public string ConnectionString { get; init; } = string.Empty;

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain discovery command timeout must be between 1 and 300 seconds.");
    }
}

public sealed class SqlServerOptionChainWorkCandidateSource
    : IOptionChainWorkCandidateSource
{
    private readonly SqlServerOptionChainWorkCandidateSourceOptions _options;

    public SqlServerOptionChainWorkCandidateSource(
        SqlServerOptionChainWorkCandidateSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<IReadOnlyCollection<OptionChainWorkCandidate>> DiscoverAsync(
        DateTimeOffset workflowCutoffUtc,
        DateTimeOffset minimumEventAtUtc,
        int maximumCount,
        string engineVersion,
        string policyVersion,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 250)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (minimumEventAtUtc > workflowCutoffUtc)
            throw new ArgumentOutOfRangeException(nameof(minimumEventAtUtc));

        const string sql = """
            SELECT TOP (@maximum_count)
                snapshot.[option_chain_snapshot_uid],
                CONCAT(exchange.[exchange_code], ':', underlying.[canonical_symbol]) AS [instrument_key],
                snapshot.[event_at_utc],
                snapshot.[received_at_utc],
                snapshot.[revision],
                snapshot.[snapshot_status],
                snapshot.[quality_status],
                snapshot.[is_point_in_time_eligible]
            FROM [market].[option_chain_snapshots] snapshot
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = snapshot.[underlying_instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = underlying.[exchange_id]
            WHERE snapshot.[snapshot_status] = 'COMPLETE'
              AND snapshot.[quality_status] = 'VALID'
              AND snapshot.[is_point_in_time_eligible] = 1
              AND snapshot.[contract_count] > 0
              AND snapshot.[strike_count] >= 3
              AND snapshot.[event_at_utc] >= @minimum_event_at_utc
              AND snapshot.[event_at_utc] <= @workflow_cutoff_utc
              AND snapshot.[received_at_utc] <= @workflow_cutoff_utc
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [intelligence].[option_chain_work_queue] queued
                  WHERE queued.[snapshot_uid] = snapshot.[option_chain_snapshot_uid]
                    AND queued.[engine_version] = @engine_version
                    AND queued.[policy_version] = @policy_version
                    AND queued.[workflow_cutoff_utc] = snapshot.[received_at_utc]
              )
            ORDER BY snapshot.[received_at_utc], snapshot.[event_at_utc],
                     snapshot.[option_chain_snapshot_uid];
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@minimum_event_at_utc", SqlDbType.DateTime2).Value = minimumEventAtUtc.UtcDateTime;
        command.Parameters.Add("@workflow_cutoff_utc", SqlDbType.DateTime2).Value = workflowCutoffUtc.UtcDateTime;
        command.Parameters.Add("@engine_version", SqlDbType.NVarChar, 64).Value = engineVersion;
        command.Parameters.Add("@policy_version", SqlDbType.NVarChar, 64).Value = policyVersion;

        var candidates = new List<OptionChainWorkCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new OptionChainWorkCandidate(
                reader.GetGuid(0),
                reader.GetString(1),
                ReadUtc(reader, 2),
                ReadUtc(reader, 3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7)));
        }

        return candidates;
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
