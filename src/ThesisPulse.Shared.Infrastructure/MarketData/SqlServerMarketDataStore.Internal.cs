using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly TimeZoneInfo IndiaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private async Task<long> ResolveSourceIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sourceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [data_source_id]
            FROM [market].[data_sources] WITH (UPDLOCK, HOLDLOCK)
            WHERE [source_code] = @source_code
              AND [is_active] = 1;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_code", SqlDbType.VarChar, 50).Value = sourceCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? throw new InvalidOperationException(
                $"Active market data source '{sourceCode}' is not seeded.")
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<InstrumentMapping> ResolveInstrumentMappingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string providerInstrumentKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                mapping.[broker_instrument_mapping_id],
                mapping.[instrument_id],
                instrument.[market_segment],
                exchange.[timezone_id]
            FROM [reference].[broker_instrument_mappings] mapping
                WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = mapping.[instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = instrument.[exchange_id]
            WHERE broker.[broker_code] = @broker_code
              AND broker.[is_active] = 1
              AND mapping.[broker_instrument_key] = @instrument_key
              AND mapping.[is_active] = 1
              AND mapping.[valid_to_date] IS NULL
              AND instrument.[status] = 'ACTIVE';
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            _options.BrokerCode;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            providerInstrumentKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                $"Active mapping for '{providerInstrumentKey}' was not found.");
        }

        return new InstrumentMapping(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private async Task<long?> ResolveTradingSessionIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InstrumentMapping mapping,
        DateOnly tradeDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) session.[trading_session_id]
            FROM [reference].[trading_sessions] session WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[exchange_calendars] calendar
                ON calendar.[exchange_calendar_id] = session.[exchange_calendar_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = calendar.[exchange_id]
            WHERE exchange.[timezone_id] = @timezone_id
              AND session.[market_segment] = @market_segment
              AND session.[session_code] = 'REGULAR'
              AND session.[valid_from_date] <= @trade_date
              AND (session.[valid_to_date] IS NULL OR session.[valid_to_date] >= @trade_date)
            ORDER BY
                CASE calendar.[status]
                    WHEN 'ACTIVE' THEN 0
                    WHEN 'DRAFT' THEN 1
                    ELSE 2
                END,
                session.[valid_from_date] DESC;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@timezone_id", SqlDbType.VarChar, 100).Value =
            mapping.TimeZoneId;
        command.Parameters.Add("@market_segment", SqlDbType.VarChar, 30).Value =
            mapping.MarketSegment;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value =
            tradeDate.ToDateTime(TimeOnly.MinValue);
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IngestionBatch> StartBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long sourceId,
        long? instrumentId,
        string dataType,
        string? timeframe,
        string ingestionMode,
        string correlationId,
        DateTimeOffset? requestedFromUtc,
        DateTimeOffset? requestedToUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[ingestion_batches]
            (
                [data_source_id], [instrument_id], [data_type], [timeframe],
                [ingestion_mode], [source_request_id],
                [requested_from_utc], [requested_to_utc], [status],
                [started_at_utc], [correlation_id], [created_by], [updated_by]
            )
            OUTPUT INSERTED.[ingestion_batch_id], INSERTED.[ingestion_batch_uid]
            VALUES
            (
                @source_id, @instrument_id, @data_type, @timeframe,
                @ingestion_mode, @source_request_id,
                @requested_from_utc, @requested_to_utc, 'STARTED',
                SYSUTCDATETIME(), @correlation_id, @actor, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            (object?)instrumentId ?? DBNull.Value;
        command.Parameters.Add("@data_type", SqlDbType.VarChar, 50).Value = dataType;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            (object?)timeframe ?? DBNull.Value;
        command.Parameters.Add("@ingestion_mode", SqlDbType.VarChar, 20).Value =
            ingestionMode;
        command.Parameters.Add("@source_request_id", SqlDbType.VarChar, 200).Value =
            $"{dataType.ToLowerInvariant()}:{Guid.NewGuid():N}";
        command.Parameters.Add("@requested_from_utc", SqlDbType.DateTime2).Value =
            requestedFromUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@requested_to_utc", SqlDbType.DateTime2).Value =
            requestedToUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(correlationId, nameof(correlationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new IngestionBatch(reader.GetInt64(0), reader.GetGuid(1));
    }

    private async Task CompleteBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long batchId,
        int received,
        int accepted,
        int duplicates,
        int rejected,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [market].[ingestion_batches]
            SET [status] = @status,
                [received_record_count] = @received,
                [accepted_record_count] = @accepted,
                [duplicate_record_count] = @duplicates,
                [rejected_record_count] = @rejected,
                [completed_at_utc] = SYSUTCDATETIME(),
                [error_message] = @error_message,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [ingestion_batch_id] = @batch_id;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
        command.Parameters.Add("@received", SqlDbType.BigInt).Value = received;
        command.Parameters.Add("@accepted", SqlDbType.BigInt).Value = accepted;
        command.Parameters.Add("@duplicates", SqlDbType.BigInt).Value = duplicates;
        command.Parameters.Add("@rejected", SqlDbType.BigInt).Value = rejected;
        command.Parameters.Add("@error_message", SqlDbType.NVarChar, 4000).Value =
            (object?)errorMessage ?? DBNull.Value;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        command.Parameters.Add("@batch_id", SqlDbType.BigInt).Value = batchId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ObservationExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long sourceId,
        string sourceEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT_BIG(*)
            FROM [market].[source_observations] WITH (UPDLOCK, HOLDLOCK)
            WHERE [data_source_id] = @source_id
              AND [source_event_id] = @source_event_id
              AND [revision] = 0;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value =
            sourceEventId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private async Task<long> InsertObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IngestionBatch batch,
        long sourceId,
        InstrumentMapping mapping,
        long? tradingSessionId,
        string dataType,
        string? timeframe,
        string sourceEventId,
        DateTimeOffset eventAtUtc,
        DateTimeOffset? publishedAtUtc,
        DateTimeOffset receivedAtUtc,
        DateOnly tradeDate,
        string sourceVersion,
        string rawPayloadJson,
        MarketDataFreshnessAssessmentV1 assessment,
        string correlationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[source_observations]
            (
                [ingestion_batch_id], [data_source_id], [instrument_id],
                [broker_instrument_mapping_id], [trading_session_id],
                [data_type], [timeframe], [source_event_id],
                [event_at_utc], [published_at_utc], [received_at_utc],
                [processed_at_utc], [trade_date], [revision], [source_version],
                [payload_contract_version], [payload_hash], [raw_payload_json],
                [quality_status], [quality_reason_codes_json],
                [is_point_in_time_eligible], [correlation_id], [created_by]
            )
            OUTPUT INSERTED.[source_observation_id]
            VALUES
            (
                @batch_id, @source_id, @instrument_id,
                @mapping_id, @session_id,
                @data_type, @timeframe, @source_event_id,
                @event_at_utc, @published_at_utc, @received_at_utc,
                SYSUTCDATETIME(), @trade_date, 0, @source_version,
                @contract_version, @payload_hash, @raw_payload_json,
                @quality_status, @reason_codes_json,
                @point_in_time_eligible, @correlation_id, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@batch_id", SqlDbType.BigInt).Value = batch.BatchId;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            mapping.InstrumentId;
        command.Parameters.Add("@mapping_id", SqlDbType.BigInt).Value =
            mapping.MappingId;
        command.Parameters.Add("@session_id", SqlDbType.BigInt).Value =
            (object?)tradingSessionId ?? DBNull.Value;
        command.Parameters.Add("@data_type", SqlDbType.VarChar, 50).Value = dataType;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            (object?)timeframe ?? DBNull.Value;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value =
            sourceEventId;
        command.Parameters.Add("@event_at_utc", SqlDbType.DateTime2).Value =
            eventAtUtc.UtcDateTime;
        command.Parameters.Add("@published_at_utc", SqlDbType.DateTime2).Value =
            publishedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value =
            receivedAtUtc.UtcDateTime;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value =
            tradeDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 100).Value =
            sourceVersion;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 50).Value =
            MarketDataContractV1.ContractVersion;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(rawPayloadJson);
        command.Parameters.Add("@raw_payload_json", SqlDbType.NVarChar, -1).Value =
            rawPayloadJson;
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
            assessment.QualityStatus;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(assessment.ReasonCodes, JsonOptions);
        command.Parameters.Add("@point_in_time_eligible", SqlDbType.Bit).Value =
            assessment.QualityStatus != MarketDataQualityStatusV1.Invalid;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(correlationId, nameof(correlationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertQualityAssessmentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long sourceId,
        InstrumentMapping mapping,
        long observationId,
        long? candleId,
        string dataType,
        string? timeframe,
        MarketDataFreshnessAssessmentV1 assessment,
        string correlationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[data_quality_assessments]
            (
                [contract_version], [environment], [instrument_id],
                [data_type], [timeframe], [data_source_id],
                [source_observation_id], [candle_id], [evaluated_at_utc],
                [freshness_basis_utc], [age_milliseconds],
                [maximum_age_milliseconds], [quality_status],
                [reason_codes_json], [is_usable_for_new_exposure],
                [is_usable_for_exit], [revision], [policy_version],
                [correlation_id], [created_by]
            )
            VALUES
            (
                @contract_version, 'PAPER', @instrument_id,
                @data_type, @timeframe, @source_id,
                @observation_id, @candle_id, @evaluated_at_utc,
                @freshness_basis_utc, @age_milliseconds,
                @maximum_age_milliseconds, @quality_status,
                @reason_codes_json, @usable_new_exposure,
                @usable_exit, 0, @policy_version,
                @correlation_id, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            MarketDataContractV1.ContractVersion;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            mapping.InstrumentId;
        command.Parameters.Add("@data_type", SqlDbType.VarChar, 50).Value = dataType;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            (object?)timeframe ?? DBNull.Value;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@observation_id", SqlDbType.BigInt).Value =
            observationId;
        command.Parameters.Add("@candle_id", SqlDbType.BigInt).Value =
            (object?)candleId ?? DBNull.Value;
        command.Parameters.Add("@evaluated_at_utc", SqlDbType.DateTime2).Value =
            assessment.EvaluatedAtUtc.UtcDateTime;
        command.Parameters.Add("@freshness_basis_utc", SqlDbType.DateTime2).Value =
            assessment.FreshnessBasisUtc.UtcDateTime;
        command.Parameters.Add("@age_milliseconds", SqlDbType.BigInt).Value =
            assessment.AgeMilliseconds;
        command.Parameters.Add("@maximum_age_milliseconds", SqlDbType.BigInt).Value =
            assessment.MaximumAgeMilliseconds;
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
            assessment.QualityStatus;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(assessment.ReasonCodes, JsonOptions);
        command.Parameters.Add("@usable_new_exposure", SqlDbType.Bit).Value =
            assessment.IsUsableForNewExposure;
        command.Parameters.Add("@usable_exit", SqlDbType.Bit).Value =
            assessment.IsUsableForExit;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value =
            assessment.PolicyVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(correlationId, nameof(correlationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertCursorAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long sourceId,
        InstrumentMapping mapping,
        string dataType,
        string? timeframe,
        string sourceEventId,
        DateTimeOffset eventAtUtc,
        DateTimeOffset receivedAtUtc,
        long batchId,
        string state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE [market].[ingestion_cursors] WITH (HOLDLOCK) AS target
            USING
            (
                SELECT
                    @source_id AS [data_source_id],
                    @instrument_id AS [instrument_id],
                    @data_type AS [data_type],
                    @timeframe AS [timeframe]
            ) AS source
            ON target.[data_source_id] = source.[data_source_id]
               AND target.[instrument_id] = source.[instrument_id]
               AND target.[data_type] = source.[data_type]
               AND
               (
                   target.[timeframe] = source.[timeframe]
                   OR (target.[timeframe] IS NULL AND source.[timeframe] IS NULL)
               )
            WHEN MATCHED THEN
                UPDATE SET
                    [cursor_state] = @state,
                    [last_source_event_id] = @source_event_id,
                    [last_event_at_utc] = @event_at_utc,
                    [last_received_at_utc] = @received_at_utc,
                    [last_successful_batch_id] = @batch_id,
                    [consecutive_failure_count] = 0,
                    [last_error_code] = NULL,
                    [last_error_message] = NULL,
                    [next_retry_at_utc] = NULL,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
            WHEN NOT MATCHED THEN
                INSERT
                (
                    [data_source_id], [instrument_id], [data_type], [timeframe],
                    [cursor_state], [last_source_event_id], [last_event_at_utc],
                    [last_received_at_utc], [last_successful_batch_id],
                    [consecutive_failure_count], [created_by], [updated_by]
                )
                VALUES
                (
                    @source_id, @instrument_id, @data_type, @timeframe,
                    @state, @source_event_id, @event_at_utc,
                    @received_at_utc, @batch_id,
                    0, @actor, @actor
                );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            mapping.InstrumentId;
        command.Parameters.Add("@data_type", SqlDbType.VarChar, 50).Value = dataType;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            (object?)timeframe ?? DBNull.Value;
        command.Parameters.Add("@state", SqlDbType.VarChar, 20).Value = state;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value =
            sourceEventId;
        command.Parameters.Add("@event_at_utc", SqlDbType.DateTime2).Value =
            eventAtUtc.UtcDateTime;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value =
            receivedAtUtc.UtcDateTime;
        command.Parameters.Add("@batch_id", SqlDbType.BigInt).Value = batchId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateOnly GetTradeDate(DateTimeOffset eventAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(eventAtUtc, IndiaTimeZone).DateTime);

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record InstrumentMapping(
        long MappingId,
        long InstrumentId,
        string MarketSegment,
        string TimeZoneId);

    private sealed record IngestionBatch(long BatchId, Guid BatchUid);
}
