using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public sealed class SqlServerOptionChainIntelligenceOutputStore : IOptionChainIntelligenceOutputStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedDirections = new(StringComparer.Ordinal)
    {
        "STRONG_LONG", "LONG", "NEUTRAL", "SHORT", "STRONG_SHORT", "NO_SIGNAL",
    };
    private static readonly HashSet<string> AllowedQualityStatuses = new(StringComparer.Ordinal)
    {
        "VALID", "DEGRADED", "INVALID",
    };

    private readonly SqlServerOptionChainIntelligenceOutputStoreOptions _options;

    public SqlServerOptionChainIntelligenceOutputStore(
        SqlServerOptionChainIntelligenceOutputStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<OptionChainAppendResult> AppendAsync(
        OptionChainPersistenceEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Output);

        var rejection = ValidateEnvelope(envelope);
        if (rejection is not null)
        {
            return Rejected(envelope.Output, rejection);
        }

        var rawJson = JsonSerializer.Serialize(envelope.Output, JsonOptions);
        var contractHash = ComputeSha256(rawJson);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existing = await FindByOutputUidAsync(
                connection,
                transaction,
                envelope.Output.OutputUid,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return string.Equals(existing.ContractHash, contractHash, StringComparison.Ordinal)
                    ? Duplicate(envelope.Output)
                    : Rejected(envelope.Output, "OUTPUT_UID_PAYLOAD_CONFLICT");
            }

            var engineId = await ResolveEngineIdAsync(connection, transaction, cancellationToken);
            var instrumentId = await ResolveInstrumentIdAsync(
                connection,
                transaction,
                envelope.Output.UnderlyingInstrumentKey,
                cancellationToken);
            if (instrumentId is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(envelope.Output, "UNDERLYING_INSTRUMENT_NOT_FOUND");
            }

            var current = await FindCurrentRevisionAsync(
                connection,
                transaction,
                engineId,
                instrumentId.Value,
                envelope.Output.AsOfUtc,
                cancellationToken);
            if (current is null && envelope.Output.Revision != 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(envelope.Output, "INITIAL_REVISION_MUST_BE_ZERO");
            }
            if (current is not null && envelope.Output.Revision <= current.Revision)
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(envelope.Output, "REVISION_NOT_NEWER");
            }

            var snapshotIds = await ResolveSnapshotIdsAsync(
                connection,
                transaction,
                envelope.Output.SourceSnapshotUids,
                cancellationToken);
            if (snapshotIds.Count != envelope.Output.SourceSnapshotUids.Count)
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(envelope.Output, "SOURCE_SNAPSHOT_NOT_FOUND");
            }

            var runId = await InsertEngineRunAsync(
                connection,
                transaction,
                engineId,
                envelope,
                cancellationToken);

            if (current is not null)
            {
                await MarkSupersededAsync(
                    connection,
                    transaction,
                    current.EngineOutputId,
                    cancellationToken);
            }

            var outputId = await InsertEngineOutputAsync(
                connection,
                transaction,
                runId,
                engineId,
                instrumentId.Value,
                envelope,
                rawJson,
                contractHash,
                current?.OutputUid,
                cancellationToken);

            await InsertSnapshotLineageAsync(
                connection,
                transaction,
                outputId,
                envelope,
                snapshotIds,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new OptionChainAppendResult(
                OptionChainAppendOutcome.Inserted,
                envelope.Output.OutputUid,
                envelope.Output.Revision,
                null);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var replay = await FindByOutputUidAsync(
                connection,
                null,
                envelope.Output.OutputUid,
                cancellationToken);
            return replay is not null &&
                   string.Equals(replay.ContractHash, contractHash, StringComparison.Ordinal)
                ? Duplicate(envelope.Output)
                : Rejected(envelope.Output, "SQL_UNIQUENESS_CONFLICT");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<OptionChainPersistenceEnvelope?> GetLatestAtOrBeforeAsync(
        OptionChainPointInTimeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.UnderlyingInstrumentKey);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT TOP (1)
                output.[raw_contract_json],
                receipt.[source_received_at_utc],
                output.[created_at_utc]
            FROM [intelligence].[engine_outputs] output
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = output.[instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = instrument.[exchange_id]
            CROSS APPLY
            (
                SELECT MAX(snapshot.[received_at_utc]) AS [source_received_at_utc]
                FROM [intelligence].[option_chain_output_snapshot_inputs] input
                INNER JOIN [market].[option_chain_snapshots] snapshot
                    ON snapshot.[option_chain_snapshot_id] = input.[option_chain_snapshot_id]
                WHERE input.[engine_output_id] = output.[engine_output_id]
            ) receipt
            WHERE engine.[engine_code] = @engine_code
              AND output.[timeframe] = 'OPTION_CHAIN'
              AND CONCAT(exchange.[exchange_code], ':', instrument.[canonical_symbol]) = @instrument_key
              AND output.[as_of_utc] <= @cutoff_utc
              AND output.[generated_at_utc] <= @cutoff_utc
              AND receipt.[source_received_at_utc] <= @cutoff_utc
            ORDER BY output.[as_of_utc] DESC, output.[revision] DESC,
                     receipt.[source_received_at_utc] DESC;
            """;

        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.AddWithValue("@engine_code", _options.EngineCode);
        command.Parameters.AddWithValue("@instrument_key", query.UnderlyingInstrumentKey);
        command.Parameters.AddWithValue("@cutoff_utc", query.WorkflowCutoffUtc.UtcDateTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var output = JsonSerializer.Deserialize<OptionChainIntelligenceOutputV1>(
            reader.GetString(0),
            JsonOptions) ?? throw new InvalidOperationException(
                "Stored option-chain contract could not be deserialized.");

        return new OptionChainPersistenceEnvelope(
            output,
            new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero));
    }

    private async Task<long> ResolveEngineIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [engine_id]
            FROM [intelligence].[engines] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_code] = @engine_code
              AND [is_active] = 1
              AND [can_create_signals] = 0
              AND [can_execute_orders] = 0;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@engine_code", _options.EngineCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException(
                "Active non-authoritative option-chain engine was not found.")
            : Convert.ToInt64(value);
    }

    private static async Task<long?> ResolveInstrumentIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string instrumentKey,
        CancellationToken cancellationToken)
    {
        var separator = instrumentKey.IndexOf(':');
        if (separator <= 0 || separator == instrumentKey.Length - 1)
        {
            return null;
        }

        const string sql = """
            SELECT instrument.[instrument_id]
            FROM [reference].[instruments] instrument WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = instrument.[exchange_id]
            WHERE exchange.[exchange_code] = @exchange_code
              AND instrument.[canonical_symbol] = @canonical_symbol
              AND instrument.[is_active] = 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@exchange_code", instrumentKey[..separator]);
        command.Parameters.AddWithValue("@canonical_symbol", instrumentKey[(separator + 1)..]);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<StoredRow?> FindByOutputUidAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid outputUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [contract_hash]
            FROM [intelligence].[engine_outputs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_output_uid] = @output_uid;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@output_uid", outputUid);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : new StoredRow(Convert.ToString(value)!);
    }

    private static async Task<CurrentRevision?> FindCurrentRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long engineId,
        long instrumentId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [engine_output_id], [engine_output_uid], [revision]
            FROM [intelligence].[engine_outputs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_id] = @engine_id
              AND [instrument_id] = @instrument_id
              AND [timeframe] = 'OPTION_CHAIN'
              AND [as_of_utc] = @as_of_utc
              AND [is_current] = 1
            ORDER BY [revision] DESC;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@engine_id", engineId);
        command.Parameters.AddWithValue("@instrument_id", instrumentId);
        command.Parameters.AddWithValue("@as_of_utc", asOfUtc.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentRevision(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2))
            : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, long>> ResolveSnapshotIdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<Guid> snapshotUids,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, long>();
        const string sql = """
            SELECT [option_chain_snapshot_id]
            FROM [market].[option_chain_snapshots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [option_chain_snapshot_uid] = @snapshot_uid;
            """;
        foreach (var uid in snapshotUids)
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.AddWithValue("@snapshot_uid", uid);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not null and not DBNull)
            {
                result[uid] = Convert.ToInt64(value);
            }
        }
        return result;
    }

    private async Task<long> InsertEngineRunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long engineId,
        OptionChainPersistenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[engine_runs]
            ([engine_run_uid], [engine_id], [environment], [engine_version],
             [configuration_version], [data_cutoff_utc], [started_at_utc],
             [completed_at_utc], [status], [correlation_id], [causation_id],
             [input_count], [output_count], [warning_count], [created_by], [updated_by])
            OUTPUT INSERTED.[engine_run_id]
            VALUES
            (NEWID(), @engine_id, @environment, @engine_version,
             @configuration_version, @data_cutoff_utc, @started_at_utc,
             @completed_at_utc, 'SUCCEEDED', @correlation_id, @causation_id,
             @input_count, 1, @warning_count, @actor, @actor);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@engine_id", engineId);
        command.Parameters.AddWithValue("@environment", _options.Environment);
        command.Parameters.AddWithValue("@engine_version", envelope.Output.EngineVersion);
        command.Parameters.AddWithValue("@configuration_version", envelope.Output.PolicyVersion);
        command.Parameters.AddWithValue("@data_cutoff_utc", envelope.Output.AsOfUtc.UtcDateTime);
        command.Parameters.AddWithValue("@started_at_utc", envelope.Output.GeneratedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@completed_at_utc", envelope.PersistedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@correlation_id", envelope.Output.MessageUid);
        command.Parameters.AddWithValue("@causation_id", envelope.Output.SourceSnapshotUids.First());
        command.Parameters.AddWithValue("@input_count", envelope.Output.SourceSnapshotUids.Count);
        command.Parameters.AddWithValue("@warning_count", envelope.Output.Warnings.Count);
        command.Parameters.AddWithValue("@actor", _options.Actor);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException("Engine run insert did not return an identifier.")
            : Convert.ToInt64(value);
    }

    private async Task<long> InsertEngineOutputAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long runId,
        long engineId,
        long instrumentId,
        OptionChainPersistenceEnvelope envelope,
        string rawJson,
        string contractHash,
        Guid? supersedesUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[engine_outputs]
            ([engine_output_uid], [message_uid], [engine_run_id], [engine_id], [instrument_id],
             [contract_version], [environment], [source_service], [source_version],
             [engine_name_snapshot], [engine_version], [timeframe], [as_of_utc],
             [generated_at_utc], [expires_at_utc], [direction], [score], [confidence],
             [data_quality_status], [data_completeness], [freshness_milliseconds],
             [missing_fields_json], [is_stale], [is_eligible_for_fusion], [revision],
             [supersedes_engine_output_uid], [is_current], [correlation_id], [causation_id],
             [metadata_json], [raw_contract_json], [contract_hash], [created_at_utc], [created_by])
            OUTPUT INSERTED.[engine_output_id]
            VALUES
            (@output_uid, @message_uid, @run_id, @engine_id, @instrument_id,
             @contract_version, @environment, @source_service, @source_version,
             @engine_name, @engine_version, 'OPTION_CHAIN', @as_of_utc,
             @generated_at_utc, @expires_at_utc, @direction, @score, @confidence,
             @quality, @completeness, @freshness_ms,
             NULL, @is_stale, @eligible, @revision,
             @supersedes_uid, 1, @correlation_id, @causation_id,
             @metadata_json, @raw_json, @contract_hash, @created_at_utc, @actor);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@output_uid", envelope.Output.OutputUid);
        command.Parameters.AddWithValue("@message_uid", envelope.Output.MessageUid);
        command.Parameters.AddWithValue("@run_id", runId);
        command.Parameters.AddWithValue("@engine_id", engineId);
        command.Parameters.AddWithValue("@instrument_id", instrumentId);
        command.Parameters.AddWithValue(
            "@contract_version",
            OptionChainIntelligenceContractV1.ContractVersion);
        command.Parameters.AddWithValue("@environment", _options.Environment);
        command.Parameters.AddWithValue("@source_service", _options.SourceService);
        command.Parameters.AddWithValue("@source_version", _options.SourceVersion);
        command.Parameters.AddWithValue("@engine_name", envelope.Output.EngineCode);
        command.Parameters.AddWithValue("@engine_version", envelope.Output.EngineVersion);
        command.Parameters.AddWithValue("@as_of_utc", envelope.Output.AsOfUtc.UtcDateTime);
        command.Parameters.AddWithValue(
            "@generated_at_utc",
            envelope.Output.GeneratedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue(
            "@expires_at_utc",
            envelope.Output.GeneratedAtUtc.Add(_options.OutputTimeToLive).UtcDateTime);
        command.Parameters.AddWithValue("@direction", envelope.Output.Direction);
        command.Parameters.AddWithValue("@score", envelope.Output.Score);
        command.Parameters.AddWithValue("@confidence", envelope.Output.Confidence);
        command.Parameters.AddWithValue("@quality", envelope.Output.DataQualityStatus);
        command.Parameters.AddWithValue("@completeness", envelope.Output.ComponentCoverage);
        command.Parameters.AddWithValue(
            "@freshness_ms",
            Math.Max(
                0L,
                (long)(envelope.Output.GeneratedAtUtc - envelope.Output.AsOfUtc)
                    .TotalMilliseconds));
        command.Parameters.AddWithValue("@is_stale", envelope.Output.IsStale);
        command.Parameters.AddWithValue("@eligible", envelope.Output.IsEligibleForFusion);
        command.Parameters.AddWithValue("@revision", envelope.Output.Revision);
        command.Parameters.AddWithValue("@supersedes_uid", (object?)supersedesUid ?? DBNull.Value);
        command.Parameters.AddWithValue("@correlation_id", envelope.Output.MessageUid);
        command.Parameters.AddWithValue("@causation_id", envelope.Output.SourceSnapshotUids.First());
        command.Parameters.AddWithValue(
            "@metadata_json",
            JsonSerializer.Serialize(
                new
                {
                    envelope.Output.SelectionAuthority,
                    envelope.Output.ExecutionAuthority,
                    sourceReceivedAtUtc = envelope.SourceReceivedAtUtc,
                },
                JsonOptions));
        command.Parameters.AddWithValue("@raw_json", rawJson);
        command.Parameters.AddWithValue("@contract_hash", contractHash);
        command.Parameters.AddWithValue("@created_at_utc", envelope.PersistedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@actor", _options.Actor);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException("Engine output insert did not return an identifier.")
            : Convert.ToInt64(value);
    }

    private async Task InsertSnapshotLineageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long outputId,
        OptionChainPersistenceEnvelope envelope,
        IReadOnlyDictionary<Guid, long> snapshotIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[option_chain_output_snapshot_inputs]
            ([engine_output_id], [option_chain_snapshot_id], [input_role],
             [input_sequence], [consumed_at_utc], [created_at_utc], [created_by])
            VALUES
            (@output_id, @snapshot_id, @input_role,
             @input_sequence, @consumed_at_utc, @created_at_utc, @created_by);
            """;
        var sequence = 0;
        foreach (var uid in envelope.Output.SourceSnapshotUids)
        {
            sequence++;
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.AddWithValue("@output_id", outputId);
            command.Parameters.AddWithValue("@snapshot_id", snapshotIds[uid]);
            command.Parameters.AddWithValue(
                "@input_role",
                sequence == 1 ? "PRIMARY" : "TERM_STRUCTURE");
            command.Parameters.AddWithValue("@input_sequence", sequence);
            command.Parameters.AddWithValue(
                "@consumed_at_utc",
                envelope.Output.GeneratedAtUtc.UtcDateTime);
            command.Parameters.AddWithValue(
                "@created_at_utc",
                envelope.PersistedAtUtc.UtcDateTime);
            command.Parameters.AddWithValue("@created_by", _options.Actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task MarkSupersededAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long outputId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [intelligence].[engine_outputs]
            SET [is_current] = 0
            WHERE [engine_output_id] = @output_id AND [is_current] = 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("@output_id", outputId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        return command;
    }

    private static string? ValidateEnvelope(OptionChainPersistenceEnvelope envelope)
    {
        var output = envelope.Output;
        if (output.OutputUid == Guid.Empty) return "OUTPUT_UID_REQUIRED";
        if (output.MessageUid == Guid.Empty) return "MESSAGE_UID_REQUIRED";
        if (output.SourceSnapshotUids.Count == 0 ||
            output.SourceSnapshotUids.Any(uid => uid == Guid.Empty))
            return "SOURCE_SNAPSHOT_REQUIRED";
        if (output.SourceSnapshotUids.Distinct().Count() != output.SourceSnapshotUids.Count)
            return "SOURCE_SNAPSHOT_DUPLICATE";
        if (string.IsNullOrWhiteSpace(output.UnderlyingInstrumentKey))
            return "UNDERLYING_INSTRUMENT_REQUIRED";
        if (!string.Equals(
                output.EngineCode,
                OptionChainIntelligenceContractV1.EngineCode,
                StringComparison.Ordinal))
            return "ENGINE_CODE_MISMATCH";
        if (string.IsNullOrWhiteSpace(output.EngineVersion)) return "ENGINE_VERSION_REQUIRED";
        if (string.IsNullOrWhiteSpace(output.PolicyVersion)) return "POLICY_VERSION_REQUIRED";
        if (!AllowedDirections.Contains(output.Direction)) return "DIRECTION_INVALID";
        if (output.Score is < -1m or > 1m) return "SCORE_INVALID";
        if (output.Confidence is < 0m or > 1m) return "CONFIDENCE_INVALID";
        if (output.ComponentCoverage is < 0m or > 1m) return "COMPONENT_COVERAGE_INVALID";
        if (!AllowedQualityStatuses.Contains(output.DataQualityStatus))
            return "DATA_QUALITY_INVALID";
        if (output.IsEligibleForFusion &&
            (output.IsStale || output.DataQualityStatus == "INVALID"))
            return "FUSION_ELIGIBILITY_INVALID";
        if (output.Revision < 0) return "REVISION_INVALID";
        if (output.GeneratedAtUtc < output.AsOfUtc) return "GENERATED_BEFORE_OBSERVATION";
        if (envelope.SourceReceivedAtUtc < output.AsOfUtc)
            return "SOURCE_RECEIVED_BEFORE_OBSERVATION";
        if (envelope.PersistedAtUtc < output.GeneratedAtUtc)
            return "PERSISTED_BEFORE_GENERATION";
        if (output.SelectionAuthority || output.ExecutionAuthority) return "AUTHORITY_DRIFT";
        return null;
    }

    private static OptionChainAppendResult Duplicate(OptionChainIntelligenceOutputV1 output) =>
        new(
            OptionChainAppendOutcome.Duplicate,
            output.OutputUid,
            output.Revision,
            "OUTPUT_ALREADY_PERSISTED");

    private static OptionChainAppendResult Rejected(
        OptionChainIntelligenceOutputV1 output,
        string reason) =>
        new(
            OptionChainAppendOutcome.Rejected,
            output.OutputUid,
            output.Revision,
            reason);

    private static string ComputeSha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record StoredRow(string ContractHash);
    private sealed record CurrentRevision(long EngineOutputId, Guid OutputUid, int Revision);
}
