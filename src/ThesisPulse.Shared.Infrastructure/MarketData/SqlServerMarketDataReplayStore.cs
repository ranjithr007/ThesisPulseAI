using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class SqlServerMarketDataReplayStore(
    SqlServerMessagingOptions options) : IMarketDataReplayStore
{
    public async Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        long afterPosition,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (afterPosition < 0 || maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        const string sql = """
            SELECT TOP (@maximum_count)
                [outbox_message_id], [message_uid], [contract_version],
                [environment], [message_type], [correlation_id], [causation_id],
                [source_service], [source_version], [generated_at_utc],
                [payload_json], [attempt_count], [published_at_utc],
                [last_error_message], [headers_json]
            FROM [operations].[outbox_messages]
            WHERE [status] = 'PUBLISHED'
              AND [outbox_message_id] > @after_position
              AND [message_type] IN
                  ('market.quote.published.v1', 'market.candle.published.v1')
            ORDER BY [outbox_message_id];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@after_position", SqlDbType.BigInt).Value = afterPosition;
        var messages = new List<OutboxMessage>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var headers = reader.IsDBNull(14) ? null : reader.GetString(14);
            var metadata = new MessageMetadata(
                reader.GetGuid(1),
                reader.GetString(4),
                reader.GetString(2),
                SqlServerMessageValues.ReadUtcDateTimeOffset(reader, 9),
                reader.GetGuid(5).ToString("D"),
                reader.IsDBNull(6) ? null : reader.GetGuid(6).ToString("D"),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(3),
                SqlServerMessageValues.ReadConfigurationVersion(headers));
            messages.Add(new OutboxMessage(
                metadata,
                reader.GetString(10),
                OutboxMessageStatus.Published,
                reader.GetInt32(11),
                SqlServerMessageValues.ReadNullableUtcDateTimeOffset(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetInt64(0)));
        }

        return messages;
    }
}
