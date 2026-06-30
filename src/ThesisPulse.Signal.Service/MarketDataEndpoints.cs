using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Signal.Service;

public static class MarketDataEndpointExtensions
{
    private const string ConsumerName = "ThesisPulse.Signal.Service.MarketData.v1";
    private const string StreamName = "market-data.v1";

    public static IEndpointRouteBuilder MapMarketDataConsumerEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/v1/market-data/quotes", async (
            MarketDataDeliveryV1<MarketQuotePublishedV1> delivery,
            HttpRequest request,
            IConfiguration configuration,
            IMarketDataConsumerSink sink,
            InboxMessageProcessor processor,
            IMarketDataConsumerCheckpointStore checkpoints,
            CancellationToken cancellationToken) =>
        {
            if (!Authorized(request, configuration))
            {
                return Results.Unauthorized();
            }

            var result = await processor.ProcessAsync(
                delivery.Envelope,
                ConsumerName,
                async (payload, token) =>
                {
                    await sink.ApplyAsync(payload, token);
                    await checkpoints.AdvanceAsync(
                        Checkpoint(delivery.StreamPosition, delivery.Envelope.Metadata),
                        token);
                },
                cancellationToken);
            return Result(result, delivery.StreamPosition);
        });

        endpoints.MapPost("/internal/v1/market-data/candles", async (
            MarketDataDeliveryV1<MarketCandlePublishedV1> delivery,
            HttpRequest request,
            IConfiguration configuration,
            IMarketDataConsumerSink sink,
            InboxMessageProcessor processor,
            IMarketDataConsumerCheckpointStore checkpoints,
            CancellationToken cancellationToken) =>
        {
            if (!Authorized(request, configuration))
            {
                return Results.Unauthorized();
            }

            var result = await processor.ProcessAsync(
                delivery.Envelope,
                ConsumerName,
                async (payload, token) =>
                {
                    await sink.ApplyAsync(payload, token);
                    await checkpoints.AdvanceAsync(
                        Checkpoint(delivery.StreamPosition, delivery.Envelope.Metadata),
                        token);
                },
                cancellationToken);
            return Result(result, delivery.StreamPosition);
        });

        endpoints.MapGet("/api/v1/market-data/consumer/status", async (
            MarketDataConsumerBuffer buffer,
            IMarketDataConsumerCheckpointStore checkpoints,
            CancellationToken cancellationToken) => Results.Ok(new
        {
            consumerName = ConsumerName,
            quoteCount = buffer.QuoteCount,
            candleCount = buffer.CandleCount,
            checkpoint = await checkpoints.GetAsync(
                ConsumerName,
                StreamName,
                "*",
                cancellationToken),
        }));

        endpoints.MapGet("/api/v1/market-data/latest/{instrumentKey}",
            (string instrumentKey, MarketDataConsumerBuffer buffer) =>
                Results.Ok(buffer.GetLatest(instrumentKey)));
        return endpoints;
    }

    private static MarketDataConsumerCheckpoint Checkpoint(
        long position,
        ThesisPulse.Shared.Contracts.Messaging.V1.MessageMetadata metadata) =>
        new(ConsumerName, StreamName, "*", position, metadata.MessageId, metadata.OccurredAtUtc);

    private static IResult Result(InboxProcessingResult result, long position) =>
        result.Outcome == InboxProcessingOutcome.Failed
            ? Results.Problem(result.Error)
            : Results.Ok(new { result.Outcome, position, result.MessageId });

    private static bool Authorized(HttpRequest request, IConfiguration configuration)
    {
        var expected = configuration["MarketDataConsumer:InternalApiKey"];
        if (string.IsNullOrWhiteSpace(expected) ||
            !request.Headers.TryGetValue("X-ThesisPulse-Internal-Key", out var supplied))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(supplied.ToString());
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length &&
            CryptographicOperations.FixedTimeEquals(left, right);
    }
}
