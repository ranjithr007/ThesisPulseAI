using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Trading.Api;

public sealed class MarketDataStreamHub : Hub
{
}

public sealed class TradingMarketDataSink(
    MarketDataConsumerBuffer buffer,
    IHubContext<MarketDataStreamHub> hub) : IMarketDataConsumerSink
{
    public async Task ApplyAsync(
        MarketQuotePublishedV1 value,
        CancellationToken cancellationToken)
    {
        await buffer.ApplyAsync(value, cancellationToken);
        await hub.Clients.All.SendAsync("quoteUpdated", value, cancellationToken);
    }

    public async Task ApplyAsync(
        MarketCandlePublishedV1 value,
        CancellationToken cancellationToken)
    {
        await buffer.ApplyAsync(value, cancellationToken);
        await hub.Clients.All.SendAsync("candleUpdated", value, cancellationToken);
    }
}

public static class MarketDataStreamExtensions
{
    private const string ConsumerName = "ThesisPulse.Trading.Api.MarketData.v1";
    private const string StreamName = "market-data.v1";

    public static IEndpointRouteBuilder MapMarketDataStream(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<MarketDataStreamHub>("/hubs/market-data");
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

        endpoints.MapGet("/api/v1/stream/market-data/status", async (
            IConfiguration configuration,
            MarketDataConsumerBuffer buffer,
            IMarketDataConsumerCheckpointStore checkpoints,
            CancellationToken cancellationToken) => Results.Ok(new
        {
            enabled = configuration.GetValue("MarketDataConsumer:Enabled", false),
            hubPath = "/hubs/market-data",
            quoteMethod = "quoteUpdated",
            candleMethod = "candleUpdated",
            quoteCount = buffer.QuoteCount,
            candleCount = buffer.CandleCount,
            checkpoint = await checkpoints.GetAsync(
                ConsumerName,
                StreamName,
                "*",
                cancellationToken),
        }));
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
        if (!configuration.GetValue("MarketDataConsumer:Enabled", false))
        {
            return false;
        }

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
