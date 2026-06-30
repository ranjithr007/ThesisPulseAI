using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public static class DerivativesIngestionEndpointExtensions
{
    public static IEndpointRouteBuilder MapDerivativesIngestionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/v1/derivatives/futures-basis",
            async (
                CanonicalFuturesBasisObservationV1 observation,
                HttpRequest request,
                MarketDataOperationsOptions options,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                var authorization = MarketDataEndpointExtensions.Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                try
                {
                    var result = await store.PersistFuturesBasisAsync(
                        observation,
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Conflict(new { detail = exception.Message });
                }
            });

        endpoints.MapPost(
            "/internal/v1/derivatives/option-chain",
            async (
                CanonicalOptionChainSnapshotV1 snapshot,
                HttpRequest request,
                MarketDataOperationsOptions options,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                var authorization = MarketDataEndpointExtensions.Authorize(request, options);
                if (authorization is not null)
                {
                    return authorization;
                }

                try
                {
                    var result = await store.PersistOptionChainAsync(
                        snapshot,
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Conflict(new { detail = exception.Message });
                }
            });

        return endpoints;
    }
}
