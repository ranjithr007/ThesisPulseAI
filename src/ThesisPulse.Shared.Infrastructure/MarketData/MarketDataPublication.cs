using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record MarketDataPublicationOptions
{
    public bool Enabled { get; init; }
    public string Environment { get; init; } = "PAPER";
    public string Producer { get; init; } = "ThesisPulse.MarketData.Service";
    public string ProducerVersion { get; init; } = "0.4.0";
    public string ConfigurationVersion { get; init; } = "platform-foundation-v1.0.0";

    public void Validate()
    {
        if (Environment is not ("PAPER" or "SHADOW" or "LIVE") ||
            string.IsNullOrWhiteSpace(Producer) ||
            string.IsNullOrWhiteSpace(ProducerVersion) ||
            string.IsNullOrWhiteSpace(ConfigurationVersion))
        {
            throw new InvalidOperationException(
                "Market Data publication configuration is invalid.");
        }
    }
}

public sealed class MarketDataPublicationFactory(
    MarketDataPublicationOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OutboxMessage? CreateQuote(
        CanonicalLiveMarketUpdateV1 update,
        MarketDataFreshnessAssessmentV1 assessment,
        string correlationId)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var payload = new MarketQuotePublishedV1(
            update.ProviderCode,
            update.ProviderInstrumentKey,
            update.EventAtUtc,
            update.ReceivedAtUtc,
            update.LastTradedPrice,
            update.LastTradedQuantity,
            update.PreviousClosePrice,
            update.OpenInterest,
            update.TotalBuyQuantity,
            update.TotalSellQuantity,
            assessment.QualityStatus,
            assessment.IsUsableForNewExposure,
            update.SourceVersion);
        var identity =
            $"quote|{update.ProviderCode}|{update.ProviderInstrumentKey}|{update.SourceEventId}";
        return Create(
            MarketDataPublicationContractV1.QuoteEventType,
            payload,
            identity,
            update.EventAtUtc,
            correlationId,
            update.SourceEventId);
    }

    public OutboxMessage? CreateCandle(
        CanonicalCandleV1 candle,
        MarketDataFreshnessAssessmentV1 assessment,
        int revision,
        string correlationId)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var payload = new MarketCandlePublishedV1(
            candle.ProviderCode,
            candle.ProviderInstrumentKey,
            candle.Timeframe,
            candle.OpenAtUtc,
            candle.CloseAtUtc,
            candle.OpenPrice,
            candle.HighPrice,
            candle.LowPrice,
            candle.ClosePrice,
            candle.VolumeQuantity,
            candle.OpenInterest,
            candle.IsClosed,
            !candle.IsClosed,
            revision,
            assessment.QualityStatus,
            assessment.IsUsableForNewExposure,
            candle.ReceivedAtUtc,
            candle.SourceVersion);
        var identity =
            $"candle|{candle.ProviderCode}|{candle.ProviderInstrumentKey}|" +
            $"{candle.Timeframe}|{candle.OpenAtUtc:O}|{revision}|{candle.IsClosed}";
        return Create(
            MarketDataPublicationContractV1.CandleEventType,
            payload,
            identity,
            candle.CloseAtUtc,
            correlationId,
            candle.SourceEventId);
    }

    private OutboxMessage Create<TPayload>(
        string eventType,
        TPayload payload,
        string identity,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        string causationId)
        where TPayload : class
    {
        var messageId = CreateStableGuid(identity);
        var metadata = new MessageMetadata(
            messageId,
            eventType,
            MarketDataPublicationContractV1.ContractVersion,
            occurredAtUtc,
            correlationId,
            causationId,
            options.Producer,
            options.ProducerVersion,
            options.Environment,
            options.ConfigurationVersion);
        return new OutboxMessage(
            metadata,
            JsonSerializer.Serialize(payload, JsonOptions),
            OutboxMessageStatus.Pending,
            AttemptCount: 0,
            PublishedAtUtc: null,
            LastError: null);
    }

    private static Guid CreateStableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public interface IMarketDataPublicationWriter
{
    Task EnqueueAsync(
        OutboxMessage? message,
        CancellationToken cancellationToken = default);
}

public sealed class MarketDataPublicationWriter(
    IOutboxStore outboxStore) : IMarketDataPublicationWriter
{
    public Task EnqueueAsync(
        OutboxMessage? message,
        CancellationToken cancellationToken = default) =>
        message is null
            ? Task.CompletedTask
            : outboxStore.AddAsync(message, cancellationToken);
}
