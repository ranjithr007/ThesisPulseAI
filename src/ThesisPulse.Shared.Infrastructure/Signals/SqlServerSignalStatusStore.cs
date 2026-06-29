using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class SqlServerSignalStatusStore : ISignalStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerSignalStoreOptions _options;

    public SqlServerSignalStatusStore(SqlServerSignalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<SignalTransitionResult> TransitionStatusAsync(
        Guid signalUid,
        SignalStatusTransitionV1 transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var duplicate = await FindTransitionAsync(
                connection,
                transaction,
                transition.TransitionUid,
                cancellationToken);

            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return duplicate.SignalUid == signalUid
                    ? duplicate with { Outcome = SignalTransitionOutcome.Duplicate }
                    : Rejected(
                        transition,
                        signalUid,
                        "transitionUid is already assigned to another signal");
            }

            var current = await LoadCurrentAsync(
                connection,
                transaction,
                signalUid,
                cancellationToken);

            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new SignalTransitionResult(
                    SignalTransitionOutcome.NotFound,
                    transition.TransitionUid,
                    signalUid,
                    SignalId: null,
                    PreviousStatus: null,
                    CurrentStatus: null,
                    EventSequence: null,
                    Reason: "Signal was not found.");
            }

            var validationError = SignalStatusTransitionRules.Validate(
                current.Status,
                transition);

            if (validationError is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(transition, signalUid, validationError, current);
            }

            if (transition.RelatedSignalUid.HasValue &&
                !await RelatedSignalExistsAsync(
                    connection,
                    transaction,
                    signalUid,
                    transition.RelatedSignalUid.Value,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return Rejected(
                    transition,
                    signalUid,
                    "relatedSignalUid must reference a different existing signal",
                    current);
            }

            var nextSequence = current.EventSequence + 1;
            await InsertTransitionAsync(
                connection,
                transaction,
                current.SignalId,
                nextSequence,
                transition,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new SignalTransitionResult(
                SignalTransitionOutcome.Applied,
                transition.TransitionUid,
                signalUid,
                current.SignalId,
                current.Status,
                transition.TargetStatus.ToUpperInvariant(),
                nextSequence,
                Reason: null);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<StoredSignal?> GetAsync(
        Guid signalUid,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
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
            WHERE s.[signal_uid] = @signal_uid;
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = signalUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? MapSignal(reader)
            : null;
    }

    private async Task<SignalTransitionResult?> FindTransitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid transitionUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                event_row.[signal_status_event_uid], signal_row.[signal_uid],
                event_row.[signal_id], event_row.[status], event_row.[event_sequence]
            FROM [intelligence].[signal_status_events] event_row WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [intelligence].[signals] signal_row
                ON signal_row.[signal_id] = event_row.[signal_id]
            WHERE event_row.[signal_status_event_uid] = @transition_uid;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@transition_uid", SqlDbType.UniqueIdentifier).Value =
            transitionUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SignalTransitionResult(
            SignalTransitionOutcome.Duplicate,
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            PreviousStatus: null,
            CurrentStatus: reader.GetString(3),
            EventSequence: reader.GetInt32(4),
            Reason: null);
    }

    private async Task<CurrentSignal?> LoadCurrentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid signalUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                signal_row.[signal_id],
                COALESCE(current_status.[status], signal_row.[initial_status]) AS [status],
                COALESCE(current_status.[event_sequence], 0) AS [event_sequence]
            FROM [intelligence].[signals] signal_row WITH (UPDLOCK, HOLDLOCK)
            OUTER APPLY
            (
                SELECT TOP (1)
                    event_row.[status], event_row.[event_sequence]
                FROM [intelligence].[signal_status_events] event_row WITH (UPDLOCK, HOLDLOCK)
                WHERE event_row.[signal_id] = signal_row.[signal_id]
                ORDER BY event_row.[event_sequence] DESC
            ) current_status
            WHERE signal_row.[signal_uid] = @signal_uid;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value = signalUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new CurrentSignal(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2))
            : null;
    }

    private async Task<bool> RelatedSignalExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid signalUid,
        Guid relatedSignalUid,
        CancellationToken cancellationToken)
    {
        if (signalUid == relatedSignalUid)
        {
            return false;
        }

        const string sql = """
            SELECT COUNT_BIG(*)
            FROM [intelligence].[signals] WITH (UPDLOCK, HOLDLOCK)
            WHERE [signal_uid] = @related_signal_uid;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@related_signal_uid", SqlDbType.UniqueIdentifier).Value =
            relatedSignalUid;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task InsertTransitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long signalId,
        int eventSequence,
        SignalStatusTransitionV1 transition,
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
                @transition_uid, @signal_id, @event_sequence, @status,
                @reason_codes_json, @occurred_at_utc, @source_service,
                @source_version, @correlation_id, @causation_id,
                @metadata_json, @actor
            );
            """;

        var reasonCodes = transition.ReasonCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var metadataJson = JsonSerializer.Serialize(
            new
            {
                relatedSignalUid = transition.RelatedSignalUid,
                values = transition.Metadata,
            },
            JsonOptions);

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@transition_uid", SqlDbType.UniqueIdentifier).Value =
            transition.TransitionUid;
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = signalId;
        command.Parameters.Add("@event_sequence", SqlDbType.Int).Value = eventSequence;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
            transition.TargetStatus.ToUpperInvariant();
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(reasonCodes, JsonOptions);
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value =
            transition.OccurredAtUtc.UtcDateTime;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            transition.SourceService;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            transition.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                transition.CorrelationId,
                nameof(transition.CorrelationId));
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            (object?)SqlServerMessageValues.ToOptionalDatabaseGuid(
                transition.CausationId) ?? DBNull.Value;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            metadataJson;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SignalTransitionResult Rejected(
        SignalStatusTransitionV1 transition,
        Guid signalUid,
        string reason,
        CurrentSignal? current = null) =>
        new(
            SignalTransitionOutcome.Rejected,
            transition.TransitionUid,
            signalUid,
            current?.SignalId,
            current?.Status,
            current?.Status,
            current?.EventSequence,
            reason);

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static StoredSignal MapSignal(SqlDataReader reader) =>
        new(
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
            CreatorEngineCode: reader.GetString(15));

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record CurrentSignal(
        long SignalId,
        string Status,
        int EventSequence);
}
