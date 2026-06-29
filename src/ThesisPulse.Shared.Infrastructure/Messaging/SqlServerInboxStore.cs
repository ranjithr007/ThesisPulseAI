using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class SqlServerInboxStore : IInboxStore
{
    private readonly SqlServerMessagingOptions _options;

    public SqlServerInboxStore(SqlServerMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<bool> TryBeginProcessingAsync(
        InboxMessage message,
        string consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        if (message.ReceivedAtUtc < message.Metadata.OccurredAtUtc)
        {
            throw new ArgumentException(
                "Inbox received time cannot be earlier than the message occurrence time.",
                nameof(message));
        }

        using var payloadDocument = JsonDocument.Parse(message.PayloadJson);
        _ = payloadDocument.RootElement.ValueKind;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var retryAffected = await TryAcquireExistingAsync(
                connection,
                transaction,
                message,
                consumer,
                cancellationToken);

            if (retryAffected == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            await InsertNewAsync(
                connection,
                transaction,
                message,
                consumer,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkProcessedAsync(
        Guid messageId,
        string consumer,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        const string sql = """
            UPDATE [operations].[inbox_messages]
            SET [status] = 'PROCESSED',
                [processed_at_utc] = @processed_at_utc,
                [dead_lettered_at_utc] = NULL,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error_code] = NULL,
                [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [message_uid] = @message_uid
              AND [consumer_name] = @consumer_name
              AND [status] = 'PROCESSING';
            """;

        await ExecuteStateUpdateAsync(
            sql,
            messageId,
            consumer,
            command => command.Parameters
                .Add("@processed_at_utc", SqlDbType.DateTime2).Value =
                    processedAtUtc.UtcDateTime,
            cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid messageId,
        string consumer,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        const string sql = """
            UPDATE [operations].[inbox_messages]
            SET [status] = CASE
                    WHEN [attempt_count] >= [max_attempts]
                        THEN 'DEAD_LETTER'
                    ELSE 'FAILED'
                END,
                [processed_at_utc] = NULL,
                [dead_lettered_at_utc] = CASE
                    WHEN [attempt_count] >= [max_attempts]
                        THEN SYSUTCDATETIME()
                    ELSE NULL
                END,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error_message] = @error,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [message_uid] = @message_uid
              AND [consumer_name] = @consumer_name
              AND [status] = 'PROCESSING';
            """;

        await ExecuteStateUpdateAsync(
            sql,
            messageId,
            consumer,
            command => command.Parameters
                .Add("@error", SqlDbType.NVarChar, 2000).Value = error,
            cancellationToken);
    }

    private async Task<int> TryAcquireExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InboxMessage message,
        string consumer,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [operations].[inbox_messages] WITH (UPDLOCK, ROWLOCK)
            SET [status] = 'PROCESSING',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = @lease_expires_at_utc,
                [processed_at_utc] = NULL,
                [dead_lettered_at_utc] = NULL,
                [last_error_code] = NULL,
                [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [message_uid] = @message_uid
              AND [consumer_name] = @consumer_name
              AND [status] IN ('RECEIVED', 'FAILED')
              AND [attempt_count] < [max_attempts]
              AND ([lease_expires_at_utc] IS NULL
                   OR [lease_expires_at_utc] <= @received_at_utc);
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        AddProcessingParameters(command, message, consumer);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertNewAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InboxMessage message,
        string consumer,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [operations].[inbox_messages]
            (
                [message_uid], [consumer_name], [contract_version], [environment],
                [message_type], [source_service], [source_version], [correlation_id],
                [causation_id], [generated_at_utc], [received_at_utc], [payload_json],
                [payload_hash], [headers_json], [status], [attempt_count], [max_attempts],
                [lease_owner], [lease_expires_at_utc], [created_by], [updated_by]
            )
            VALUES
            (
                @message_uid, @consumer_name, @contract_version, @environment,
                @message_type, @source_service, @source_version, @correlation_id,
                @causation_id, @generated_at_utc, @received_at_utc, @payload_json,
                @payload_hash, @headers_json, 'PROCESSING', 1, @max_attempts,
                @lease_owner, @lease_expires_at_utc, @actor, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        AddProcessingParameters(command, message, consumer);
        AddMetadataParameters(command, message.Metadata);
        command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
            message.PayloadJson;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(message.PayloadJson);
        command.Parameters.Add("@headers_json", SqlDbType.NVarChar, -1).Value =
            SqlServerMessageValues.BuildHeadersJson(message.Metadata);
        command.Parameters.Add("@max_attempts", SqlDbType.Int).Value =
            _options.MaxAttempts;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddProcessingParameters(
        SqlCommand command,
        InboxMessage message,
        string consumer)
    {
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            message.Metadata.MessageId;
        command.Parameters.Add("@consumer_name", SqlDbType.VarChar, 200).Value =
            consumer;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value =
            message.ReceivedAtUtc.UtcDateTime;
        command.Parameters.Add("@lease_owner", SqlDbType.VarChar, 200).Value =
            _options.InstanceName;
        command.Parameters.Add("@lease_expires_at_utc", SqlDbType.DateTime2).Value =
            message.ReceivedAtUtc.Add(_options.LeaseDuration).UtcDateTime;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
    }

    private static void AddMetadataParameters(
        SqlCommand command,
        MessageMetadata metadata)
    {
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            metadata.ContractVersion;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value =
            metadata.Environment;
        command.Parameters.Add("@message_type", SqlDbType.VarChar, 200).Value =
            metadata.EventType;
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
        command.Parameters.Add("@generated_at_utc", SqlDbType.DateTime2).Value =
            metadata.OccurredAtUtc.UtcDateTime;
    }

    private async Task ExecuteStateUpdateAsync(
        string sql,
        Guid messageId,
        string consumer,
        Action<SqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            messageId;
        command.Parameters.Add("@consumer_name", SqlDbType.VarChar, 200).Value =
            consumer;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
        configure(command);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new KeyNotFoundException(
                $"No processing inbox message '{messageId}' for '{consumer}' was found.");
        }
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static bool IsUniqueConstraintViolation(SqlException exception) =>
        exception.Number is 2601 or 2627;
}
