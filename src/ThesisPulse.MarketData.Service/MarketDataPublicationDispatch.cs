using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.MarketData.Service;

public sealed record MarketDataDispatchOptions
{
    public bool Enabled { get; init; }
    public string? InternalApiKey { get; init; }
    public Uri? SignalServiceBaseUrl { get; init; }
    public Uri? TradingApiBaseUrl { get; init; }
    public bool AiFeatureFactoryEnabled { get; init; }
    public Uri? AiServiceBaseUrl { get; init; }
    public int BatchSize { get; init; } = 100;
    public int PollIntervalMilliseconds { get; init; } = 1000;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InternalApiKey) ||
            SignalServiceBaseUrl is null ||
            TradingApiBaseUrl is null ||
            (AiFeatureFactoryEnabled && AiServiceBaseUrl is null) ||
            BatchSize is < 1 or > 1000 ||
            PollIntervalMilliseconds is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                "Market Data publication dispatch configuration is invalid.");
        }
    }
}

public interface IMarketDataPendingPublicationStore
{
    Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken);
}

public sealed class InMemoryMarketDataPendingPublicationStore(
    IOutboxStore outboxStore) : IMarketDataPendingPublicationStore
{
    public async Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var messages = await outboxStore.GetPendingAsync(
            Math.Min(maximumCount * 4, 4000),
            cancellationToken);
        return messages
            .Where(message => MarketDataPublicationContractV1.EventTypes.Contains(
                message.Metadata.EventType))
            .Select((message, index) => message with { StreamPosition = index + 1L })
            .Take(maximumCount)
            .ToArray();
    }

    public Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken) =>
        outboxStore.MarkPublishedAsync(messageId, publishedAtUtc, cancellationToken);

    public Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken) =>
        outboxStore.MarkFailedAsync(messageId, error, cancellationToken);
}

public sealed class SqlServerMarketDataPendingPublicationStore(
    SqlServerMessagingOptions options,
    IOutboxStore outboxStore) : IMarketDataPendingPublicationStore
{
    public async Task<IReadOnlyCollection<OutboxMessage>> LoadAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                [outbox_message_id], [message_uid], [contract_version],
                [environment], [message_type], [correlation_id], [causation_id],
                [source_service], [source_version], [generated_at_utc],
                [payload_json], [status], [attempt_count], [published_at_utc],
                [last_error_message], [headers_json]
            FROM [operations].[outbox_messages] WITH (READPAST)
            WHERE [destination] = 'MARKET_DATA_FANOUT'
              AND [status] IN ('PENDING', 'FAILED')
              AND [not_before_utc] <= SYSUTCDATETIME()
              AND [attempt_count] < [max_attempts]
            ORDER BY [outbox_message_id];
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        var messages = new List<OutboxMessage>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var headers = reader.IsDBNull(15) ? null : reader.GetString(15);
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
                ParseStatus(reader.GetString(11)),
                reader.GetInt32(12),
                SqlServerMessageValues.ReadNullableUtcDateTimeOffset(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetInt64(0)));
        }

        return messages;
    }

    public Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken) =>
        outboxStore.MarkPublishedAsync(messageId, publishedAtUtc, cancellationToken);

    public Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken) =>
        outboxStore.MarkFailedAsync(messageId, error, cancellationToken);

    private static OutboxMessageStatus ParseStatus(string status) => status switch
    {
        "PENDING" => OutboxMessageStatus.Pending,
        "FAILED" => OutboxMessageStatus.Failed,
        _ => throw new InvalidOperationException(
            $"Unsupported dispatch status '{status}'."),
    };
}

public sealed class MarketDataFanoutClient(
    IHttpClientFactory httpClientFactory,
    MarketDataDispatchOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task SendAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var path = message.Metadata.EventType switch
        {
            MarketDataPublicationContractV1.QuoteEventType =>
                "/internal/v1/market-data/quotes",
            MarketDataPublicationContractV1.CandleEventType =>
                "/internal/v1/market-data/candles",
            _ => throw new InvalidOperationException(
                $"Unsupported market event '{message.Metadata.EventType}'."),
        };

        await SendToAsync(
            "SignalServiceMarketData",
            options.SignalServiceBaseUrl!,
            path,
            message,
            cancellationToken);
        await SendToAsync(
            "TradingApiMarketData",
            options.TradingApiBaseUrl!,
            path,
            message,
            cancellationToken);

        if (options.AiFeatureFactoryEnabled &&
            message.Metadata.EventType.Equals(
                MarketDataPublicationContractV1.CandleEventType,
                StringComparison.OrdinalIgnoreCase))
        {
            await SendToAsync(
                "AiFeatureFactoryMarketData",
                options.AiServiceBaseUrl!,
                "/internal/v1/market-data/candles",
                message,
                cancellationToken);
        }
    }

    private async Task SendToAsync(
        string clientName,
        Uri baseUrl,
        string path,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUrl, path));
        request.Headers.Add("X-ThesisPulse-Internal-Key", options.InternalApiKey);
        request.Content = JsonContent.Create(
            BuildDelivery(message),
            options: JsonOptions);
        var client = httpClientFactory.CreateClient(clientName);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static object BuildDelivery(OutboxMessage message)
    {
        if (message.Metadata.EventType.Equals(
                MarketDataPublicationContractV1.QuoteEventType,
                StringComparison.OrdinalIgnoreCase))
        {
            var payload = JsonSerializer.Deserialize<MarketQuotePublishedV1>(
                message.PayloadJson,
                JsonOptions) ?? throw new InvalidDataException("Quote payload is invalid.");
            return new MarketDataDeliveryV1<MarketQuotePublishedV1>(
                message.StreamPosition,
                new EventEnvelope<MarketQuotePublishedV1>(message.Metadata, payload));
        }

        var candle = JsonSerializer.Deserialize<MarketCandlePublishedV1>(
            message.PayloadJson,
            JsonOptions) ?? throw new InvalidDataException("Candle payload is invalid.");
        return new MarketDataDeliveryV1<MarketCandlePublishedV1>(
            message.StreamPosition,
            new EventEnvelope<MarketCandlePublishedV1>(message.Metadata, candle));
    }
}

public sealed class MarketDataPublicationDispatchWorker(
    IMarketDataPendingPublicationStore store,
    MarketDataFanoutClient fanoutClient,
    MarketDataDispatchOptions options,
    IClock clock,
    ILogger<MarketDataPublicationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var messages = await store.LoadAsync(options.BatchSize, stoppingToken);
            foreach (var message in messages)
            {
                try
                {
                    await fanoutClient.SendAsync(message, stoppingToken);
                    await store.MarkPublishedAsync(
                        message.Metadata.MessageId,
                        clock.UtcNow,
                        stoppingToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        exception,
                        "Market Data publication {MessageId} failed.",
                        message.Metadata.MessageId);
                    await store.MarkFailedAsync(
                        message.Metadata.MessageId,
                        exception.Message,
                        stoppingToken);
                }
            }

            await Task.Delay(options.PollIntervalMilliseconds, stoppingToken);
        }
    }
}
