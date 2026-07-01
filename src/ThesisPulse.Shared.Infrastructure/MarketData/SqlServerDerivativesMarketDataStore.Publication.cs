using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    private async Task EnqueueOptionChainPublicationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OutboxMessage? message,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        if (message is null)
        {
            return;
        }

        using var payload = JsonDocument.Parse(message.PayloadJson);
        _ = payload.RootElement.ValueKind;

        const string sql = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM [operations].[outbox_messages] WITH (UPDLOCK, HOLDLOCK)
                WHERE [message_uid] = @message_uid
            )
            BEGIN
                INSERT INTO [operations].[outbox_messages]
                (
                    [message_uid], [contract_version], [environment], [message_type],
                    [destination], [partition_key], [idempotency_key],
                    [correlation_id], [causation_id], [source_service],
                    [source_version], [generated_at_utc], [not_before_utc],
                    [payload_json], [payload_hash], [headers_json], [status],
                    [attempt_count], [max_attempts], [created_by], [updated_by]
                )
                VALUES
                (
                    @message_uid, @contract_version, @environment, @message_type,
                    'MARKET_DATA_FANOUT', @partition_key, @idempotency_key,
                    @correlation_id, @causation_id, @source_service,
                    @source_version, @generated_at_utc, @generated_at_utc,
                    @payload_json, @payload_hash, @headers_json, 'PENDING',
                    0, 5, @actor, @actor
                );
            END;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            message.Metadata.MessageId;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            message.Metadata.ContractVersion;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value =
            message.Metadata.Environment;
        command.Parameters.Add("@message_type", SqlDbType.VarChar, 200).Value =
            message.Metadata.EventType;
        command.Parameters.Add("@partition_key", SqlDbType.VarChar, 200).Value =
            partitionKey;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            message.Metadata.MessageId.ToString("D");
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                message.Metadata.CorrelationId,
                nameof(message.Metadata.CorrelationId));
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToOptionalDatabaseGuid(message.Metadata.CausationId)
            ?? (object)DBNull.Value;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value =
            message.Metadata.Producer;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            message.Metadata.ProducerVersion;
        command.Parameters.Add("@generated_at_utc", SqlDbType.DateTime2).Value =
            message.Metadata.OccurredAtUtc.UtcDateTime;
        command.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
            message.PayloadJson;
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
            SqlServerMessageValues.ComputePayloadHash(message.PayloadJson);
        command.Parameters.Add("@headers_json", SqlDbType.NVarChar, -1).Value =
            SqlServerMessageValues.BuildHeadersJson(message.Metadata);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
