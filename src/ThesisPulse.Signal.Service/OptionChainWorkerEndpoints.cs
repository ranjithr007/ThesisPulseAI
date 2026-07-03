namespace ThesisPulse.Signal.Service;

public static class OptionChainWorkerEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/worker/metrics", async (
            IOptionChainWorkQueue queue,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var metrics = await queue.GetMetricsAsync(timeProvider.GetUtcNow(), cancellationToken);
            return Results.Ok(metrics);
        });

        endpoints.MapGet("/api/v1/internal/option-chain/evidence/latest/{instrumentKey}", async (
            string instrumentKey,
            DateTimeOffset? cutoffUtc,
            IOptionChainIntelligenceOutputStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var cutoff = cutoffUtc ?? timeProvider.GetUtcNow();
            var envelope = await store.GetLatestAtOrBeforeAsync(
                new OptionChainPointInTimeQuery(instrumentKey, cutoff),
                cancellationToken);
            if (envelope is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                envelope.Output.OutputUid,
                envelope.Output.Revision,
                envelope.Output.UnderlyingInstrumentKey,
                envelope.Output.AsOfUtc,
                envelope.Output.GeneratedAtUtc,
                envelope.SourceReceivedAtUtc,
                envelope.PersistedAtUtc,
                envelope.Output.Direction,
                envelope.Output.Score,
                envelope.Output.Confidence,
                envelope.Output.QualityStatus,
                envelope.Output.FusionEligible,
                selectionAuthority = false,
                executionAuthority = false,
            });
        });

        return endpoints;
    }
}
