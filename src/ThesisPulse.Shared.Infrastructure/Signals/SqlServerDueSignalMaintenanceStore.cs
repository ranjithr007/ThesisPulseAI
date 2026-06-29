using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class SqlServerDueSignalMaintenanceStore : IDueSignalMaintenanceStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerSignalStoreOptions _options;

    public SqlServerDueSignalMaintenanceStore(SqlServerSignalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<ExpireDueSignalsResultV1> ExpireDueAsync(
        ExpireDueSignalsRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var dueSignals = await SelectDueSignalsAsync(
                connection,
                transaction,
                request,
                cancellationToken);
            var expired = new List<ExpiredSignalV1>(dueSignals.Count);

            foreach (var dueSignal in dueSignals)
            {
                var transitionUid = Guid.NewGuid();
                var nextSequence = dueSignal.EventSequence + 1;

                await InsertExpiryEventAsync(
                    connection,
                    transaction,
                    dueSignal,
                    transitionUid,
                    nextSequence,
                    request,
                    cancellationToken);

                expired.Add(new ExpiredSignalV1(
                    transitionUid,
                    dueSignal.SignalUid,
                    dueSignal.SignalId,
                    dueSignal.Status,
                    SignalStatusV1.Expired,
                    nextSequence));
            }

            await transaction.CommitAsync(cancellationToken);
            return new ExpireDueSignalsResultV1(
                request.AsOfUtc,
                dueSignals.Count,
                expired.Count,
                expired);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlyCollection<DueSignal>> SelectDueSignalsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ExpireDueSignalsRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                signal_row.[signal_id],
                signal_row.[signal_uid],
                COALESCE(current_status.[status], signal_row.[initial_status]) AS [status],
                COALESCE(current_status.[event_sequence], 0) AS [event_sequence],
                COALESCE(current_status.[occurred_at_utc], signal_row.[generated_at_utc])
                    AS [last_status_at_utc]
            FROM [intelligence].[signals] signal_row WITH (UPDLOCK, READPAST)
            OUTER APPLY
            (
                SELECT TOP (1)
                    event_row.[status],
                    event_row.[event_sequence],
                    event_row.[occurred_at_utc]
                FROM [intelligence].[signal_status_events] event_row
                    WITH (UPDLOCK, READPAST)
                WHERE event_row.[signal_id] = signal_row.[signal_id]
                ORDER BY event_row.[event_sequence] DESC
            ) current_status
            WHERE signal_row.[valid_until_utc] <= @as_of_utc
              AND COALESCE(current_status.[status], signal_row.[initial_status])
                  IN ('CANDIDATE', 'VALIDATED')
              AND COALESCE(
                    current_status.[occurred_at_utc],
                    signal_row.[generated_at_utc]) <= @as_of_utc
            ORDER BY signal_row.[valid_until_utc], signal_row.[signal_id];
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value =
            request.MaximumCount;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value =
            request.AsOfUtc.UtcDateTime;

        var signals = new List<DueSignal>(request.MaximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            signals.Add(new DueSignal(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return signals;
    }

    private async Task InsertExpiryEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DueSignal signal,
        Guid transitionUid,
        int eventSequence,
        ExpireDueSignalsRequestV1 request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [intelligence].[signal_status_events]
            (
                [signal_status_event_uid], [signal_id], [event_sequence], [status],
                [reason_codes_json], [occurred_at_utc], [source_service],
                [source_version], [correlation_id], [causation_id],
                [metadata_json], [created_by]
            )
            VALUES
            (
                @transition_uid, @signal_id, @event_sequence, 'EXPIRED',
                @reason_codes_json, @occurred_at_utc, @source_service,
                @source_version, @correlation_id, NULL,
                @metadata_json, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@transition_uid", SqlDbType.UniqueIdentifier).Value =
            transitionUid;
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signal.SignalId;
        command.Parameters.Add("@event_sequence", SqlDbType.Int).Value = eventSequence;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            "[\"VALIDITY_WINDOW_ELAPSED\"]";
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value =
            request.AsOfUtc.UtcDateTime;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            request.SourceService;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            request.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                request.CorrelationId,
                nameof(request.CorrelationId));
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(
                new
                {
                    job = "signal-expiry",
                    scheduledAsOfUtc = request.AsOfUtc,
                },
                JsonOptions);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static void ValidateRequest(ExpireDueSignalsRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AsOfUtc == default)
        {
            throw new ArgumentException("asOfUtc is required", nameof(request));
        }

        if (request.MaximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCount));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceService);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
    }

    private sealed record DueSignal(
        long SignalId,
        Guid SignalUid,
        string Status,
        int EventSequence);
}
