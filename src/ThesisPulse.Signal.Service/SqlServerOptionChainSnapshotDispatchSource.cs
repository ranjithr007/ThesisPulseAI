using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Signal.Service;

public sealed record SqlServerOptionChainSnapshotDispatchSourceOptions
{
    public string ConnectionString { get; init; } = string.Empty;

    public string BrokerCode { get; init; } = "UPSTOX";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerCode);
        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain snapshot load timeout must be between 1 and 300 seconds.");
    }
}

public sealed class SqlServerOptionChainSnapshotDispatchSource
    : IOptionChainSnapshotDispatchSource
{
    private readonly SqlServerOptionChainSnapshotDispatchSourceOptions _options;

    public SqlServerOptionChainSnapshotDispatchSource(
        SqlServerOptionChainSnapshotDispatchSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<OptionChainSnapshotDispatchV1?> LoadAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (workItem.WorkUid == Guid.Empty || workItem.SnapshotUid == Guid.Empty)
            return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem.InstrumentKey);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string headerSql = """
            SELECT
                snapshot.[option_chain_snapshot_id],
                snapshot.[option_chain_snapshot_uid],
                CONCAT(exchange.[exchange_code], ':', underlying.[canonical_symbol]) AS [instrument_key],
                snapshot.[expiry_date],
                snapshot.[event_at_utc],
                snapshot.[received_at_utc],
                snapshot.[underlying_price],
                snapshot.[snapshot_status],
                snapshot.[quality_status],
                snapshot.[is_point_in_time_eligible],
                snapshot.[revision],
                snapshot.[calculation_source_version]
            FROM [market].[option_chain_snapshots] snapshot
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = snapshot.[underlying_instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = underlying.[exchange_id]
            WHERE snapshot.[option_chain_snapshot_uid] = @snapshot_uid
              AND CONCAT(exchange.[exchange_code], ':', underlying.[canonical_symbol]) = @instrument_key
              AND snapshot.[event_at_utc] <= @workflow_cutoff_utc
              AND snapshot.[received_at_utc] <= @workflow_cutoff_utc
              AND snapshot.[snapshot_status] = 'COMPLETE'
              AND snapshot.[quality_status] = 'VALID'
              AND snapshot.[is_point_in_time_eligible] = 1;
            """;

        await using var headerCommand = CreateCommand(connection, headerSql);
        headerCommand.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = workItem.SnapshotUid;
        headerCommand.Parameters.Add("@instrument_key", SqlDbType.NVarChar, 200).Value = workItem.InstrumentKey;
        headerCommand.Parameters.Add("@workflow_cutoff_utc", SqlDbType.DateTime2).Value = workItem.WorkflowCutoffUtc.UtcDateTime;

        await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var snapshotId = reader.GetInt64(0);
        var snapshotUid = reader.GetGuid(1);
        var instrumentKey = reader.GetString(2);
        var expiryDate = DateOnly.FromDateTime(reader.GetDateTime(3));
        var eventAtUtc = ReadUtc(reader, 4);
        var receivedAtUtc = ReadUtc(reader, 5);
        var underlyingPrice = reader.GetDecimal(6);
        var snapshotStatus = reader.GetString(7);
        var qualityStatus = reader.GetString(8);
        var eligible = reader.GetBoolean(9);
        var revision = reader.GetInt32(10);
        var calculationSourceVersion = reader.IsDBNull(11) ? null : reader.GetString(11);
        await reader.DisposeAsync();

        const string entriesSql = """
            SELECT
                contract.[derivative_contract_uid],
                COALESCE(
                    broker_mapping.[broker_instrument_key],
                    CONCAT('INSTRUMENT_ID:', entry.[instrument_id])) AS [instrument_key],
                entry.[strike_price],
                entry.[option_type],
                entry.[last_price],
                entry.[volume_quantity],
                entry.[open_interest],
                entry.[implied_volatility],
                entry.[delta],
                contract.[contract_multiplier],
                entry.[quality_status],
                entry.[greeks_source_version]
            FROM [market].[option_chain_entries] entry
            INNER JOIN [reference].[derivative_contracts] contract
                ON contract.[derivative_contract_id] = entry.[derivative_contract_id]
            OUTER APPLY
            (
                SELECT TOP (1) mapping.[broker_instrument_key]
                FROM [reference].[broker_instrument_mappings] mapping
                INNER JOIN [reference].[brokers] broker
                    ON broker.[broker_id] = mapping.[broker_id]
                WHERE mapping.[instrument_id] = entry.[instrument_id]
                  AND broker.[broker_code] = @broker_code
                  AND broker.[is_active] = 1
                  AND mapping.[is_active] = 1
                  AND mapping.[valid_to_date] IS NULL
                ORDER BY mapping.[valid_from_date] DESC
            ) broker_mapping
            WHERE entry.[option_chain_snapshot_id] = @snapshot_id
            ORDER BY entry.[strike_price], entry.[option_type],
                     contract.[derivative_contract_uid];
            """;

        await using var entriesCommand = CreateCommand(connection, entriesSql);
        entriesCommand.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
        entriesCommand.Parameters.Add("@broker_code", SqlDbType.VarChar, 50).Value = _options.BrokerCode;
        var entries = new List<OptionChainSnapshotDispatchEntryV1>();
        await using var entriesReader = await entriesCommand.ExecuteReaderAsync(cancellationToken);
        while (await entriesReader.ReadAsync(cancellationToken))
        {
            entries.Add(new OptionChainSnapshotDispatchEntryV1(
                entriesReader.GetGuid(0),
                entriesReader.GetString(1),
                expiryDate,
                entriesReader.GetDecimal(2),
                entriesReader.GetString(3),
                ReadNullableDecimal(entriesReader, 4),
                ReadNullableDecimal(entriesReader, 5),
                ReadNullableDecimal(entriesReader, 6),
                ReadNullableDecimal(entriesReader, 7),
                ReadNullableDecimal(entriesReader, 8),
                entriesReader.GetDecimal(9),
                entriesReader.GetString(10),
                entriesReader.IsDBNull(11) ? null : entriesReader.GetString(11)));
        }

        if (entries.Count == 0)
            return null;
        if (entries.Any(entry => entry.ExpiryDate != expiryDate ||
            entry.DerivativeContractUid == Guid.Empty ||
            entry.StrikePrice <= 0 ||
            entry.OptionType is not ("CALL" or "PUT") ||
            entry.QualityStatus is not ("VALID" or "DEGRADED" or "INVALID")))
        {
            return null;
        }

        return new OptionChainSnapshotDispatchV1(
            workItem.WorkUid,
            snapshotUid,
            instrumentKey,
            expiryDate,
            eventAtUtc,
            receivedAtUtc,
            underlyingPrice,
            snapshotStatus,
            qualityStatus,
            eligible,
            revision,
            entries,
            calculationSourceVersion);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        return command;
    }

    private static decimal? ReadNullableDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
