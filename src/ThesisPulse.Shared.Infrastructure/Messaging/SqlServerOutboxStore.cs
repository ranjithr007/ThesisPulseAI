using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class SqlServerOutboxStore : IOutboxStore
{
    private readonly SqlServerMessagingOptions _options;

    public SqlServerOutboxStore(SqlServerMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task AddAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Status != OutboxMessageStatus.Pending)
        {
            throw new ArgumentException(
                "New durable outbox messages must start in Pending status.",
                nameof(message));
        }

        using var payloadDocument = JsonDocument.Parse(message.PayloadJson);
        _ = payloadDocument.RootElement.ValueKind;

        const string sql = """
            INSERT INTO [operations].[outbox_messages]
            (
                [message_uid], [contract_version], [environment], [message_type],
                [destination], [idempotency_key], [correlation_id], [causation_id],
                [source_service], [source_version], [generated_at_utc], [not_before_utc],
                [payload_json], [payload_hash], [headers_json], [status], [attempt_count],
                [max_attempts], [created_by], [updated_by]
            )
            VALUES
            (
                @message_uid, @contract_version, @environment, @message_type,
                @destination, @idempotency_key, @correlation_id, @causation_id,
                @source_service, @source_version, @generated_at_utc, @not_before_utc,
                @payload_json, @payload_hash, @headers_json, 'PENDING', 0,
                @max_attempts, @actor, @actor
            );
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);

        AddMetadataParameters(command, message.Metadata);
        command.Parameters.Add("@destination", SqlDbType.VarChar, 200).Value =
            message.Metadata.EventType;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            message.Metadata.MessageId.ToString("D");
        command.Parameters.Add("@not_before_utc", SqlDbType.DateTime2).Value =
            message.Metadata.OccurredAtUtc.UtcDateTime;
        command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
            message.PayloadJson;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(message.PayloadJson);
        command.Parameters.Add("@headers_json", SqlDbType.NVarChar, -1).Value =
            SqlServerMessageValues.BuildHeadersJson(message.Metadata);
        command.Parameters.Add("@max_attempts", SqlDbType.Int).Value =
            _options.MaxAttempts;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            throw new InvalidOperationException(
                $"Outbox message '{message.Metadata.MessageId}' already exists.",
                exception);
        }
    }

    public async Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                "Maximum count must be greater than zero.");
        }

        const string sql = """
            SELECT TOP (@maximum_count)
                [message_uid], [contract_version], [environment], [message_type],
                [correlation_id], [causation_id], [source_service], [source_version],
                [generated_at_utc], [payload_json], [status], [attempt_count],
                [published_at_utc], [last_error_message], [headers_json]
            FROM [operations].[outbox_messages] WITH (READPAST)
            WHERE [status] IN ('PENDING', 'FAILED')
              AND [not_before_utc] <= SYSUTCDATETIME()
              AND ([expires_at_utc] IS NULL OR [expires_at_utc] > SYSUTCDATETIME())
              AND [attempt_count] < [max_attempts]
            ORDER BY [not_before_utc], [outbox_message_id];
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;

        var messages = new List<OutboxMessage>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var headersJson = reader.IsDBNull(14) ? null : reader.GetString(14);
            var metadata = new MessageMetadata(
                MessageId: reader.GetGuid(0),
                EventType: reader.GetString(3),
                ContractVersion: reader.GetString(1),
                OccurredAtUtc: SqlServerMessageValues.ReadUtcDateTimeOffset(reader, 8),
                CorrelationId: reader.GetGuid(4).ToString("D"),
                CausationId: reader.IsDBNull(5)
                    ? null
                    : reader.GetGuid(5).ToString("D"),
                Producer: reader.GetString(6),
                ProducerVersion: reader.GetString(7),
                Environment: reader.GetString(2),
                ConfigurationVersion:
                    SqlServerMessageValues.ReadConfigurationVersion(headersJson));

            messages.Add(new OutboxMessage(
                Metadata: metadata,
                PayloadJson: reader.GetString(9),
                Status: ParseStatus(reader.GetString(10)),
                AttemptCount: reader.GetInt32(11),
                PublishedAtUtc:
                    SqlServerMessageValues.ReadNullableUtcDateTimeOffset(reader, 12),
                LastError: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return messages;
    }

    public async Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE [operations].[outbox_messages]
            SET [status] = 'PUBLISHED',
                [attempt_count] = [attempt_count] + 1,
                [published_at_utc] = @published_at_utc,
                [dead_lettered_at_utc] = NULL,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error_code] = NULL,
                [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [message_uid] = @message_uid
              AND [status] IN ('PENDING', 'FAILED', 'IN_FLIGHT')
              AND [attempt_count] < [max_attempts];
            """;

        await ExecuteStateUpdateAsync(
            sql,
            messageId,
            command => command.Parameters
                .Add("@published_at_utc", SqlDbType.DateTime2).Value =
                    publishedAtUtc.UtcDateTime,
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        const string sql = """
            UPDATE [operations].[outbox_messages]
            SET [attempt_count] = [attempt_count] + 1,
                [status] = CASE
                    WHEN [attempt_count] + 1 >= [max_attempts]
                        THEN 'DEAD_LETTER'
                    ELSE 'FAILED'
                END,
                [dead_lettered_at_utc] = CASE
                    WHEN [attempt_count] + 1 >= [max_attempts]
                        THEN SYSUTCDATETIME()
                    ELSE NULL
                END,
                [published_at_utc] = NULL,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error_message] = @error,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [message_uid] = @message_uid
              AND [status] IN ('PENDING', 'FAILED', 'IN_FLIGHT')
              AND [attempt_count] < [max_attempts];
            """;

        await ExecuteStateUpdateAsync(
            sql,
            messageId,
            command => command.Parameters
                .Add("@error", SqlDbType.NVarChar, 2000).Value = error,
            cancellationToken);
    }

    private async Task ExecuteStateUpdateAsync(
        string sql,
        Guid messageId,
        Action<SqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            messageId;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
        configure(command);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new KeyNotFoundException(
                $"No dispatchable outbox message '{messageId}' was found.");
        }
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) =>
        new(sql, connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static void AddMetadataParameters(
        SqlCommand command,
        MessageMetadata metadata)
    {
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            metadata.MessageId;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            metadata.ContractVersion;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value =
            metadata.Environment;
        command.Parameters.Add("@message_type", SqlDbType.VarChar, 200).Value =
            metadata.EventType;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                metadata.CorrelationId,
                nameof(metadata.CorrelationId));
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToOptionalDatabaseGuid(metadata.CausationId)
            ?? (object)DBNull.Value;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            metadata.Producer;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            metadata.ProducerVersion;
        command.Parameters.Add("@generated_at_utc", SqlDbType.DateTime2).Value =
            metadata.OccurredAtUtc.UtcDateTime;
    }

    private static bool IsUniqueConstraintViolation(SqlException exception) =>
        exception.Number is 2601 or 2627;

    private static OutboxMessageStatus ParseStatus(string status) => status switch
    {
        "PENDING" => OutboxMessageStatus.Pending,
        "PUBLISHED" => OutboxMessageStatus.Published,
        "FAILED" => OutboxMessageStatus.Failed,
        "DEAD_LETTER" => OutboxMessageStatus.DeadLetter,
        _ => throw new InvalidOperationException(
            $"Unsupported outbox status '{status}'."),
    };
}
