using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public static class MarketDataPublicationEndpointExtensions
{
    public static IEndpointRouteBuilder MapMarketDataPublicationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/publications/status", (
            MarketDataPublicationOptions publication,
            MarketDataDispatchOptions dispatch) => Results.Ok(new
        {
            publicationEnabled = publication.Enabled,
            dispatchEnabled = dispatch.Enabled,
            contractVersion = MarketDataPublicationContractV1.ContractVersion,
            eventTypes = MarketDataPublicationContractV1.EventTypes,
        }));

        endpoints.MapGet("/internal/v1/publications/replay", async (
            long? afterPosition,
            int? limit,
            HttpRequest request,
            MarketDataDispatchOptions options,
            IMarketDataReplayStore replayStore,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(request, options.InternalApiKey))
            {
                return Results.Unauthorized();
            }

            var after = afterPosition ?? 0;
            var requested = Math.Clamp(limit ?? 100, 1, 999);
            var messages = await replayStore.LoadAsync(
                after,
                requested + 1,
                cancellationToken);
            var page = messages.Take(requested).ToArray();
            return Results.Ok(new MarketDataReplayPageV1(
                after,
                page.LastOrDefault()?.StreamPosition ?? after,
                page.Length,
                messages.Count > requested,
                page.Select(message => new MarketDataReplayItemV1(
                    message.StreamPosition,
                    message.Metadata.EventType,
                    message.Metadata.ContractVersion,
                    message.Metadata.MessageId,
                    message.Metadata.OccurredAtUtc,
                    message.Metadata.CorrelationId,
                    message.Metadata.CausationId,
                    message.Metadata.Producer,
                    message.Metadata.ProducerVersion,
                    message.Metadata.Environment,
                    message.Metadata.ConfigurationVersion,
                    message.PayloadJson)).ToArray()));
        });

        return endpoints;
    }

    private static bool IsAuthorized(HttpRequest request, string? expectedKey)
    {
        if (string.IsNullOrWhiteSpace(expectedKey) ||
            !request.Headers.TryGetValue("X-ThesisPulse-Internal-Key", out var supplied))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
