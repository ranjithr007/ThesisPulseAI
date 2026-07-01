using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.MarketData.Service;

public static class DerivativesQueryEndpointExtensions
{
    public static IEndpointRouteBuilder MapDerivativesQueryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/derivatives/contracts",
            async (
                string underlyingInstrumentKey,
                DateOnly? expiryDate,
                string? contractClass,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(underlyingInstrumentKey))
                {
                    return MarketDataEndpointExtensions.Validation(
                        "underlyingInstrumentKey is required.");
                }

                try
                {
                    var contracts = await store.GetContractsAsync(
                        underlyingInstrumentKey,
                        expiryDate,
                        contractClass,
                        cancellationToken);
                    return Results.Ok(new
                    {
                        underlyingInstrumentKey,
                        expiryDate,
                        contractClass,
                        contracts,
                        count = contracts.Count,
                        policyVersion = DerivativesMarketDataContractV1.CatalogPolicyVersion,
                        selectionAuthority = false,
                    });
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
            });

        endpoints.MapGet(
            "/api/v1/derivatives/expiries",
            async (
                string underlyingInstrumentKey,
                string? marketSegment,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(underlyingInstrumentKey))
                {
                    return MarketDataEndpointExtensions.Validation(
                        "underlyingInstrumentKey is required.");
                }

                try
                {
                    var expiries = await store.GetExpiriesAsync(
                        underlyingInstrumentKey,
                        marketSegment,
                        cancellationToken);
                    return Results.Ok(new
                    {
                        underlyingInstrumentKey,
                        marketSegment,
                        expiries,
                        count = expiries.Count,
                        policyVersion = DerivativesMarketDataContractV1.CatalogPolicyVersion,
                    });
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
            });

        endpoints.MapGet(
            "/api/v1/derivatives/futures-basis/latest",
            async (
                string futureInstrumentKey,
                DateTimeOffset? asOfUtc,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(futureInstrumentKey))
                {
                    return MarketDataEndpointExtensions.Validation(
                        "futureInstrumentKey is required.");
                }

                try
                {
                    var observation = await store.GetLatestFuturesBasisAsync(
                        futureInstrumentKey,
                        asOfUtc,
                        cancellationToken);
                    return observation is null
                        ? Results.NotFound(new
                        {
                            detail = "Futures-basis observation was not found.",
                        })
                        : Results.Ok(observation);
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
            });

        endpoints.MapGet(
            "/api/v1/derivatives/option-chain/latest",
            async (
                string underlyingInstrumentKey,
                DateOnly expiryDate,
                DateTimeOffset? asOfUtc,
                IDerivativesMarketDataStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(underlyingInstrumentKey) ||
                    expiryDate == default)
                {
                    return MarketDataEndpointExtensions.Validation(
                        "underlyingInstrumentKey and expiryDate are required.");
                }

                try
                {
                    var snapshot = await store.GetLatestOptionChainAsync(
                        underlyingInstrumentKey,
                        expiryDate,
                        asOfUtc,
                        cancellationToken);
                    return snapshot is null
                        ? Results.NotFound(new
                        {
                            detail = "Option-chain snapshot was not found.",
                        })
                        : Results.Ok(snapshot);
                }
                catch (ArgumentException exception)
                {
                    return MarketDataEndpointExtensions.Validation(exception.Message);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new { detail = exception.Message });
                }
            });

        return endpoints;
    }
}
