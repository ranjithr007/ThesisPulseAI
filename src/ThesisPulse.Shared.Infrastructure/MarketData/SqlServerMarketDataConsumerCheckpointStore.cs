using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class SqlServerMarketDataConsumerCheckpointStore(
    SqlServerMessagingOptions options) : IMarketDataConsumerCheckpointStore
{
    public async Task<MarketDataConsumerCheckpoint?> GetAsync(
        string consumerName,
        string streamName,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT [last_outbox_message_id], [last_message_uid],
                   [last_occurred_at_utc]
            FROM [operations].[consumer_checkpoints]
            WHERE [consumer_name] = @consumer_name
              AND [stream_name] = @stream_name
              AND [partition_key] = @partition_key;
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        AddScope(command, consumerName, streamName, partitionKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MarketDataConsumerCheckpoint(
            consumerName,
            streamName,
            partitionKey,
            reader.GetInt64(0),
            reader.GetGuid(1),
            SqlServerMessageValues.ReadUtcDateTimeOffset(reader, 2));
    }

    public async Task AdvanceAsync(
        MarketDataConsumerCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        const string sql = """
            MERGE [operations].[consumer_checkpoints] WITH (HOLDLOCK) AS target
            USING
            (
                SELECT @consumer_name AS [consumer_name],
                       @stream_name AS [stream_name],
                       @partition_key AS [partition_key]
            ) AS source
            ON target.[consumer_name] = source.[consumer_name]
               AND target.[stream_name] = source.[stream_name]
               AND target.[partition_key] = source.[partition_key]
            WHEN MATCHED AND target.[last_outbox_message_id] < @position THEN
                UPDATE SET
                    [last_outbox_message_id] = @position,
                    [last_message_uid] = @message_uid,
                    [last_occurred_at_utc] = @occurred_at_utc,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
            WHEN NOT MATCHED THEN
                INSERT
                (
                    [consumer_name], [stream_name], [partition_key],
                    [last_outbox_message_id], [last_message_uid],
                    [last_occurred_at_utc], [created_by], [updated_by]
                )
                VALUES
                (
                    @consumer_name, @stream_name, @partition_key,
                    @position, @message_uid, @occurred_at_utc,
                    @actor, @actor
                );
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        AddScope(
            command,
            checkpoint.ConsumerName,
            checkpoint.StreamName,
            checkpoint.PartitionKey);
        command.Parameters.Add("@position", SqlDbType.BigInt).Value =
            checkpoint.LastPosition;
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            checkpoint.LastMessageId;
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value =
            checkpoint.LastOccurredAtUtc.UtcDateTime;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql) =>
        new(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };

    private static void AddScope(
        SqlCommand command,
        string consumerName,
        string streamName,
        string partitionKey)
    {
        command.Parameters.Add("@consumer_name", SqlDbType.VarChar, 200).Value =
            consumerName;
        command.Parameters.Add("@stream_name", SqlDbType.VarChar, 200).Value =
            streamName;
        command.Parameters.Add("@partition_key", SqlDbType.VarChar, 200).Value =
            partitionKey;
    }
}
