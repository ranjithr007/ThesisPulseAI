using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public sealed record LiveFeedIngestionRequest(
    DateTimeOffset ReceivedAtUtc,
    string CorrelationId,
    IReadOnlyCollection<UpstoxDecodedMarketFeed> Feeds);

public static class MarketDataEndpointExtensions
{
    public static IEndpointRouteBuilder MapMarketDataEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/candles",
            async (
                string instrumentKey,
                string timeframe,
                int? limit,
                IMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(instrumentKey) ||
                    !MarketDataContractV1.Timeframes.Contains(timeframe))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = new[]
                        {
                            "instrumentKey is required and timeframe must be one of 1m, 5m, 15m, 1h, 1d.",
                        },
                    });
                }

                var candles = await store.GetLatestCandlesAsync(
                    instrumentKey,
                    timeframe,
                    limit ?? 200,
                    cancellationToken);
                return Results.Ok(new
                {
                    instrumentKey,
                    timeframe,
                    candles,
                    count = candles.Count,
                });
            });

        endpoints.MapGet(
            "/api/v1/jobs",
            (MarketDataJobState state) => Results.Ok(new
            {
                jobs = state.GetSnapshots(),
            }));

        endpoints.MapPost(
            "/internal/v1/instruments/synchronize",
            async (
                HttpRequest request,
                MarketDataOperationsOptions options,
                MarketDataOrchestrator orchestrator,
                CancellationToken cancellationToken) =>
            {
                var authorization = Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                var correlationId = GetCorrelationId(request);
                var result = await orchestrator.SynchronizeInstrumentsAsync(
                    correlationId,
                    cancellationToken);
                return Results.Ok(result);
            });

        endpoints.MapPost(
            "/internal/v1/candles/backfill",
            async (
                HistoricalCandleRequestV1 requestBody,
                HttpRequest request,
                MarketDataOperationsOptions options,
                MarketDataOrchestrator orchestrator,
                CancellationToken cancellationToken) =>
            {
                var authorization = Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                var result = await orchestrator.BackfillAsync(
                    requestBody,
                    cancellationToken);
                return Results.Ok(result);
            });

        endpoints.MapPost(
            "/internal/v1/live/normalize-and-ingest",
            async (
                LiveFeedIngestionRequest requestBody,
                HttpRequest request,
                MarketDataOperationsOptions options,
                MarketDataOrchestrator orchestrator,
                CancellationToken cancellationToken) =>
            {
                var authorization = Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                if (requestBody.ReceivedAtUtc == default ||
                    string.IsNullOrWhiteSpace(requestBody.CorrelationId) ||
                    requestBody.Feeds.Count == 0)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = new[]
                        {
                            "receivedAtUtc, correlationId and at least one decoded feed are required.",
                        },
                    });
                }

                var result = await orchestrator.NormalizeAndPersistLiveAsync(
                    requestBody.Feeds,
                    requestBody.ReceivedAtUtc,
                    requestBody.CorrelationId,
                    cancellationToken);
                return Results.Ok(result);
            });

        endpoints.MapGet(
            "/internal/v1/live/authorized-uri",
            async (
                HttpRequest request,
                MarketDataOperationsOptions options,
                IMarketDataProvider provider,
                CancellationToken cancellationToken) =>
            {
                var authorization = Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                var uri = await provider.GetLiveFeedAuthorizedUriAsync(
                    cancellationToken);
                return Results.Ok(new
                {
                    authorizedUri = uri.ToString(),
                    singleUse = true,
                });
            });

        return endpoints;
    }

    private static IResult? Authorize(
        HttpRequest request,
        MarketDataOperationsOptions options)
    {
        if (!options.Enabled)
        {
            return Results.Problem(
                title: "Market data operations are disabled",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!request.Headers.TryGetValue(
                "X-ThesisPulse-Internal-Key",
                out var suppliedValues))
        {
            return Results.Unauthorized();
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedValues.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(options.InternalApiKey!);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes)
            ? null
            : Results.Unauthorized();
    }

    private static string GetCorrelationId(HttpRequest request) =>
        request.Headers.TryGetValue("X-Correlation-ID", out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : Guid.NewGuid().ToString("D");
}
