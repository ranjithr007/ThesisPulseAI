using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class SqlServerSignalStore : ISignalStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerSignalStoreOptions _options;

    public SqlServerSignalStore(SqlServerSignalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<SignalSaveResult> SaveAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var instrumentKey = InstrumentKey.Parse(envelope.Payload.InstrumentKey);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existing = await FindExistingAsync(
                connection,
                transaction,
                envelope.Metadata.MessageId,
                envelope.Payload.SignalUid,
                cancellationToken);

            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new SignalSaveResult(
                    SignalSaveOutcome.Duplicate,
                    existing.Value.SignalUid,
                    existing.Value.SignalId);
            }

            var creatorEngineId = await ResolveCreatorEngineAsync(
                connection,
                transaction,
                envelope.Metadata.Producer,
                cancellationToken);
            var instrument = await ResolveInstrumentAsync(
                connection,
                transaction,
                instrumentKey,
                envelope.Payload.GeneratedAtUtc,
                cancellationToken);

            var signalId = await InsertSignalAsync(
                connection,
                transaction,
                envelope,
                creatorEngineId,
                instrument,
                cancellationToken);

            await InsertConfirmationTimeframesAsync(
                connection,
                transaction,
                signalId,
                envelope.Payload.ConfirmationTimeframes,
                cancellationToken);
            await InsertEvidenceAsync(
                connection,
                transaction,
                signalId,
                envelope.Payload.Evidence,
                cancellationToken);
            await InsertInitialStatusAsync(
                connection,
                transaction,
                signalId,
                envelope.Metadata,
                envelope.Payload.GeneratedAtUtc,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new SignalSaveResult(
                SignalSaveOutcome.Created,
                envelope.Payload.SignalUid,
                signalId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await FindExistingWithoutTransactionAsync(
                connection,
                envelope.Metadata.MessageId,
                envelope.Payload.SignalUid,
                cancellationToken);

            if (existing is not null)
            {
                return new SignalSaveResult(
                    SignalSaveOutcome.Duplicate,
                    existing.Value.SignalUid,
                    existing.Value.SignalId);
            }

            throw;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<StoredSignal>> GetLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        const string sql = """
            SELECT TOP (@maximum_count)
                s.[signal_id], s.[signal_uid], s.[message_uid],
                e.[exchange_code], i.[canonical_symbol],
                s.[strategy_code], s.[strategy_version], s.[direction],
                s.[primary_timeframe], s.[strength], s.[confidence],
                COALESCE(current_status.[status], s.[initial_status]) AS [status],
                s.[generated_at_utc], s.[valid_until_utc],
                s.[source_service], creator.[engine_code]
            FROM [intelligence].[signals] s
            INNER JOIN [reference].[instruments] i
                ON i.[instrument_id] = s.[instrument_id]
            INNER JOIN [reference].[exchanges] e
                ON e.[exchange_id] = i.[exchange_id]
            INNER JOIN [intelligence].[engines] creator
                ON creator.[engine_id] = s.[creator_engine_id]
            OUTER APPLY
            (
                SELECT TOP (1) status_event.[status]
                FROM [intelligence].[signal_status_events] status_event
                WHERE status_event.[signal_id] = s.[signal_id]
                ORDER BY status_event.[event_sequence] DESC
            ) current_status
            ORDER BY s.[generated_at_utc] DESC, s.[signal_id] DESC;
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;

        var signals = new List<StoredSignal>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            signals.Add(new StoredSignal(
                SignalId: reader.GetInt64(0),
                SignalUid: reader.GetGuid(1),
                MessageId: reader.GetGuid(2),
                InstrumentKey: $"{reader.GetString(3)}|{reader.GetString(4)}",
                StrategyCode: reader.GetString(5),
                StrategyVersion: reader.GetString(6),
                Direction: reader.GetString(7),
                PrimaryTimeframe: reader.GetString(8),
                Strength: reader.GetDecimal(9),
                Confidence: reader.GetDecimal(10),
                Status: reader.GetString(11),
                GeneratedAtUtc: ReadUtc(reader, 12),
                ValidUntilUtc: ReadUtc(reader, 13),
                Producer: reader.GetString(14),
                CreatorEngineCode: reader.GetString(15)));
        }

        return signals;
    }

    private async Task<long> InsertSignalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EventEnvelope<SignalGeneratedV1> envelope,
        long creatorEngineId,
        InstrumentResolution instrument,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[signals]
            (
                [signal_uid], [message_uid], [creator_engine_id], [instrument_id],
                [contract_version], [environment], [source_service], [source_version],
                [strategy_code], [strategy_version], [direction], [primary_timeframe],
                [strength], [confidence], [entry_opens_at_utc], [entry_closes_at_utc],
                [reference_price], [minimum_price], [maximum_price],
                [invalidation_price], [invalidation_reason],
                [expected_holding_period_minutes], [initial_status],
                [status_reasons_json], [generated_at_utc], [valid_until_utc],
                [supersedes_signal_uid], [fusion_policy_version], [correlation_id],
                [causation_id], [metadata_json], [raw_contract_json],
                [contract_hash], [created_by]
            )
            OUTPUT INSERTED.[signal_id]
            VALUES
            (
                @signal_uid, @message_uid, @creator_engine_id, @instrument_id,
                @contract_version, @environment, @source_service, @source_version,
                @strategy_code, @strategy_version, @direction, @primary_timeframe,
                @strength, @confidence, @entry_opens_at_utc, @entry_closes_at_utc,
                @reference_price, @minimum_price, @maximum_price,
                @invalidation_price, @invalidation_reason,
                @expected_holding_period_minutes, 'CANDIDATE',
                @status_reasons_json, @generated_at_utc, @valid_until_utc,
                NULL, @fusion_policy_version, @correlation_id,
                @causation_id, @metadata_json, @raw_contract_json,
                @contract_hash, @actor
            );
            """;

        var payload = envelope.Payload;
        var rawContractJson = JsonSerializer.Serialize(payload, JsonOptions);
        var metadataJson = JsonSerializer.Serialize(
            new
            {
                originalInstrumentKey = payload.InstrumentKey,
                canonicalInstrumentKey = instrument.CanonicalKey,
                configurationVersion = envelope.Metadata.ConfigurationVersion,
            },
            JsonOptions);

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value =
            payload.SignalUid;
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            envelope.Metadata.MessageId;
        command.Parameters.Add("@creator_engine_id", SqlDbType.BigInt).Value =
            creatorEngineId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            instrument.InstrumentId;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            envelope.Metadata.ContractVersion;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value =
            envelope.Metadata.Environment;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            envelope.Metadata.Producer;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            envelope.Metadata.ProducerVersion;
        command.Parameters.Add("@strategy_code", SqlDbType.VarChar, 100).Value =
            payload.StrategyCode;
        command.Parameters.Add("@strategy_version", SqlDbType.VarChar, 50).Value =
            payload.StrategyVersion;
        command.Parameters.Add("@direction", SqlDbType.VarChar, 10).Value =
            payload.Direction;
        command.Parameters.Add("@primary_timeframe", SqlDbType.VarChar, 20).Value =
            payload.PrimaryTimeframe;
        command.Parameters.Add("@strength", SqlDbType.Decimal).Value = payload.Strength;
        command.Parameters["@strength"].Precision = 9;
        command.Parameters["@strength"].Scale = 8;
        command.Parameters.Add("@confidence", SqlDbType.Decimal).Value = payload.Confidence;
        command.Parameters["@confidence"].Precision = 9;
        command.Parameters["@confidence"].Scale = 8;
        AddDateTime(command, "@entry_opens_at_utc", payload.EntryOpensAtUtc);
        AddDateTime(command, "@entry_closes_at_utc", payload.EntryClosesAtUtc);
        AddPrice(command, "@reference_price", payload.ReferencePrice);
        AddNullablePrice(command, "@minimum_price", payload.MinimumPrice);
        AddNullablePrice(command, "@maximum_price", payload.MaximumPrice);
        AddPrice(command, "@invalidation_price", payload.InvalidationPrice);
        command.Parameters.Add("@invalidation_reason", SqlDbType.NVarChar, 1000).Value =
            payload.InvalidationReason;
        command.Parameters.Add("@expected_holding_period_minutes", SqlDbType.Int).Value =
            payload.ExpectedHoldingPeriodMinutes;
        command.Parameters.Add("@status_reasons_json", SqlDbType.NVarChar, -1).Value =
            "[\"PHASE1_INTAKE_ACCEPTED\"]";
        AddDateTime(command, "@generated_at_utc", payload.GeneratedAtUtc);
        AddDateTime(command, "@valid_until_utc", payload.ValidUntilUtc);
        command.Parameters.Add("@fusion_policy_version", SqlDbType.VarChar, 50).Value =
            (object?)payload.FusionPolicyVersion ?? DBNull.Value;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                envelope.Metadata.CorrelationId,
                nameof(envelope.Metadata.CorrelationId));
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            (object?)SqlServerMessageValues.ToOptionalDatabaseGuid(
                envelope.Metadata.CausationId) ?? DBNull.Value;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            metadataJson;
        command.Parameters.Add("@raw_contract_json", SqlDbType.NVarChar, -1).Value =
            rawContractJson;
        command.Parameters.Add("@contract_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawContractJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> ResolveCreatorEngineAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string producer,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [engine_id]
            FROM [intelligence].[engines] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_code] = @engine_code
              AND [owner_service] = @owner_service
              AND [engine_role] = 'FUSION'
              AND [can_create_signals] = 1
              AND [can_execute_orders] = 0
              AND [is_active] = 1;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@engine_code", SqlDbType.VarChar, 100).Value =
            _options.CreatorEngineCode;
        command.Parameters.Add("@owner_service", SqlDbType.VarChar, 100).Value = producer;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? throw new InvalidOperationException(
                $"Producer '{producer}' is not authorized by active fusion engine " +
                $"'{_options.CreatorEngineCode}'.")
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<InstrumentResolution> ResolveInstrumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InstrumentKey key,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (2)
                i.[instrument_id], e.[exchange_code], i.[canonical_symbol]
            FROM [reference].[instruments] i WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[exchanges] e
                ON e.[exchange_id] = i.[exchange_id]
            WHERE e.[exchange_code] = @exchange_code
              AND e.[is_active] = 1
              AND i.[status] = 'ACTIVE'
              AND i.[valid_from_date] <= @generated_date
              AND (i.[valid_to_date] IS NULL OR i.[valid_to_date] >= @generated_date)
              AND
              (
                  i.[canonical_symbol] = @lookup_value
                  OR i.[display_name] = @lookup_value
                  OR REPLACE(UPPER(i.[display_name]), ' ', '') =
                     REPLACE(UPPER(@lookup_value), ' ', '')
              )
            ORDER BY
                CASE WHEN i.[canonical_symbol] = @lookup_value THEN 0 ELSE 1 END,
                i.[valid_from_date] DESC;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@exchange_code", SqlDbType.VarChar, 20).Value =
            key.ExchangeCode;
        command.Parameters.Add("@lookup_value", SqlDbType.NVarChar, 200).Value =
            key.LookupValue;
        command.Parameters.Add("@generated_date", SqlDbType.Date).Value =
            generatedAtUtc.UtcDateTime.Date;

        var matches = new List<InstrumentResolution>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new InstrumentResolution(
                reader.GetInt64(0),
                $"{reader.GetString(1)}|{reader.GetString(2)}"));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException(
                $"Active instrument '{key.ExchangeCode}|{key.LookupValue}' was not found."),
            _ => throw new InvalidOperationException(
                $"Instrument '{key.ExchangeCode}|{key.LookupValue}' is ambiguous."),
        };
    }

    private async Task InsertConfirmationTimeframesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long signalId,
        IReadOnlyCollection<string> timeframes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[signal_confirmation_timeframes]
                ([signal_id], [timeframe], [created_by])
            VALUES (@signal_id, @timeframe, @actor);
            """;

        foreach (var timeframe in timeframes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
            command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value = timeframe;
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertEvidenceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long signalId,
        IReadOnlyCollection<SignalEvidenceV1> evidenceItems,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[signal_evidence]
                ([signal_id], [evidence_code], [evidence_message], [impact], [weight], [created_by])
            VALUES (@signal_id, @code, @message, @impact, @weight, @actor);
            """;

        foreach (var evidence in evidenceItems)
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
            command.Parameters.Add("@code", SqlDbType.VarChar, 100).Value = evidence.Code;
            command.Parameters.Add("@message", SqlDbType.NVarChar, 1000).Value =
                evidence.Message;
            command.Parameters.Add("@impact", SqlDbType.VarChar, 30).Value = evidence.Impact;
            var weight = command.Parameters.Add("@weight", SqlDbType.Decimal);
            weight.Precision = 9;
            weight.Scale = 8;
            weight.Value = (object?)evidence.Weight ?? DBNull.Value;
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertInitialStatusAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long signalId,
        MessageMetadata metadata,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[signal_status_events]
            (
                [signal_id], [event_sequence], [status], [reason_codes_json],
                [occurred_at_utc], [source_service], [source_version],
                [correlation_id], [causation_id], [metadata_json], [created_by]
            )
            VALUES
            (
                @signal_id, 0, 'CANDIDATE', @reason_codes_json,
                @occurred_at_utc, @source_service, @source_version,
                @correlation_id, @causation_id, @metadata_json, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            "[\"PHASE1_INTAKE_ACCEPTED\"]";
        AddDateTime(command, "@occurred_at_utc", occurredAtUtc);
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            metadata.Producer;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            metadata.ProducerVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                metadata.CorrelationId,
                nameof(metadata.CorrelationId));
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            (object?)SqlServerMessageValues.ToOptionalDatabaseGuid(metadata.CausationId)
            ?? DBNull.Value;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(
                new { configurationVersion = metadata.ConfigurationVersion },
                JsonOptions);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(long SignalId, Guid SignalUid)?> FindExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid messageId,
        Guid signalUid,
        CancellationToken cancellationToken) =>
        await FindExistingCoreAsync(
            connection,
            transaction,
            messageId,
            signalUid,
            cancellationToken);

    private async Task<(long SignalId, Guid SignalUid)?> FindExistingWithoutTransactionAsync(
        SqlConnection connection,
        Guid messageId,
        Guid signalUid,
        CancellationToken cancellationToken) =>
        await FindExistingCoreAsync(
            connection,
            transaction: null,
            messageId,
            signalUid,
            cancellationToken);

    private async Task<(long SignalId, Guid SignalUid)?> FindExistingCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid messageId,
        Guid signalUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [signal_id], [signal_uid]
            FROM [intelligence].[signals] WITH (UPDLOCK, HOLDLOCK)
            WHERE [message_uid] = @message_uid OR [signal_uid] = @signal_uid
            ORDER BY CASE WHEN [message_uid] = @message_uid THEN 0 ELSE 1 END;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value = messageId;
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = signalUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetGuid(1))
            : null;
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static void AddDateTime(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value.UtcDateTime;

    private static void AddPrice(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 6;
        parameter.Value = value;
    }

    private static void AddNullablePrice(
        SqlCommand command,
        string name,
        decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 6;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record InstrumentResolution(long InstrumentId, string CanonicalKey);
}
