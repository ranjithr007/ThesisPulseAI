using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class InMemoryMarketDataRecoveryStateStore :
    IMarketDataRecoveryStateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public Task RecordDetectedAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        SetAsync(gap, "DETECTED", cancellationToken);

    public Task RecordRecoveredAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        SetAsync(gap, "RECOVERED", cancellationToken);

    public Task RecordFailureAsync(
        MarketDataGap gap,
        string correlationId,
        string error,
        CancellationToken cancellationToken = default) =>
        SetAsync(gap, "FAILED", cancellationToken);

    private Task SetAsync(
        MarketDataGap gap,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _states[BuildKey(gap)] = status;
        }

        return Task.CompletedTask;
    }

    private static string BuildKey(MarketDataGap gap) =>
        $"{gap.ProviderInstrumentKey}|{gap.Timeframe}|" +
        $"{gap.GapStartUtc:O}|{gap.GapEndUtc:O}";
}

public sealed class SqlServerMarketDataRecoveryStateStore(
    SqlServerMarketDataOptions options) : IMarketDataRecoveryStateStore
{
    public Task RecordDetectedAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(gap, correlationId, "DETECTED", null, cancellationToken);

    public Task RecordRecoveredAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(gap, correlationId, "RECOVERED", null, cancellationToken);

    public Task RecordFailureAsync(
        MarketDataGap gap,
        string correlationId,
        string error,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(gap, correlationId, "FAILED", error, cancellationToken);

    private async Task UpsertAsync(
        MarketDataGap gap,
        string correlationId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @source_id bigint =
            (
                SELECT [data_source_id]
                FROM [market].[data_sources]
                WHERE [source_code] = @source_code
                  AND [is_active] = 1
            );

            DECLARE @instrument_id bigint =
            (
                SELECT mapping.[instrument_id]
                FROM [reference].[broker_instrument_mappings] mapping
                INNER JOIN [reference].[brokers] broker
                    ON broker.[broker_id] = mapping.[broker_id]
                WHERE broker.[broker_code] = @broker_code
                  AND mapping.[broker_instrument_key] = @instrument_key
                  AND mapping.[is_active] = 1
                  AND mapping.[valid_to_date] IS NULL
            );

            IF @source_id IS NULL OR @instrument_id IS NULL
                THROW 61020, 'Market data recovery identity could not be resolved.', 1;

            MERGE [market].[data_gap_events] WITH (HOLDLOCK) AS target
            USING
            (
                SELECT
                    @source_id AS [data_source_id],
                    @instrument_id AS [instrument_id],
                    @timeframe AS [timeframe],
                    @gap_start_utc AS [gap_start_utc],
                    @gap_end_utc AS [gap_end_utc]
            ) AS source
            ON target.[data_source_id] = source.[data_source_id]
               AND target.[instrument_id] = source.[instrument_id]
               AND target.[timeframe] = source.[timeframe]
               AND target.[gap_start_utc] = source.[gap_start_utc]
               AND target.[gap_end_utc] = source.[gap_end_utc]
            WHEN MATCHED THEN
                UPDATE SET
                    [status] = @status,
                    [recovery_attempt_count] =
                        target.[recovery_attempt_count] +
                        CASE WHEN @status IN ('RECOVERED', 'FAILED') THEN 1 ELSE 0 END,
                    [last_recovery_at_utc] =
                        CASE WHEN @status IN ('RECOVERED', 'FAILED')
                             THEN SYSUTCDATETIME()
                             ELSE target.[last_recovery_at_utc]
                        END,
                    [recovered_at_utc] =
                        CASE WHEN @status = 'RECOVERED'
                             THEN SYSUTCDATETIME()
                             ELSE target.[recovered_at_utc]
                        END,
                    [last_error_message] = @error,
                    [correlation_id] = @correlation_id,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
            WHEN NOT MATCHED THEN
                INSERT
                (
                    [data_source_id], [instrument_id], [timeframe],
                    [gap_start_utc], [gap_end_utc], [expected_record_count],
                    [detected_at_utc], [status], [recovery_attempt_count],
                    [last_recovery_at_utc], [recovered_at_utc],
                    [last_error_message], [correlation_id],
                    [created_by], [updated_by]
                )
                VALUES
                (
                    @source_id, @instrument_id, @timeframe,
                    @gap_start_utc, @gap_end_utc, @expected_record_count,
                    SYSUTCDATETIME(), @status,
                    CASE WHEN @status IN ('RECOVERED', 'FAILED') THEN 1 ELSE 0 END,
                    CASE WHEN @status IN ('RECOVERED', 'FAILED')
                         THEN SYSUTCDATETIME() ELSE NULL END,
                    CASE WHEN @status = 'RECOVERED'
                         THEN SYSUTCDATETIME() ELSE NULL END,
                    @error, @correlation_id, @actor, @actor
                );
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@source_code", SqlDbType.VarChar, 50).Value =
            options.HistoricalSourceCode;
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            options.BrokerCode;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            gap.ProviderInstrumentKey;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            gap.Timeframe;
        command.Parameters.Add("@gap_start_utc", SqlDbType.DateTime2).Value =
            gap.GapStartUtc.UtcDateTime;
        command.Parameters.Add("@gap_end_utc", SqlDbType.DateTime2).Value =
            gap.GapEndUtc.UtcDateTime;
        command.Parameters.Add("@expected_record_count", SqlDbType.Int).Value =
            gap.ExpectedRecordCount;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 4000).Value =
            (object?)error ?? DBNull.Value;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(correlationId, nameof(correlationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
