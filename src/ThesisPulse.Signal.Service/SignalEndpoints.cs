using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public static class SignalEndpoints
{
    public static IEndpointRouteBuilder MapSignalEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/v1/signals/intake",
            async (
                EventEnvelope<SignalGeneratedV1> envelope,
                SignalIntakeCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                var result = await coordinator.ProcessAsync(
                    envelope,
                    cancellationToken);

                return result.Outcome switch
                {
                    SignalIntakeOutcome.Accepted => Results.Accepted(
                        $"/api/v1/signals/{result.SignalUid}",
                        ToResponse("ACCEPTED", result)),
                    SignalIntakeOutcome.DuplicateMessage => Results.Ok(
                        ToResponse("DUPLICATE_MESSAGE_IGNORED", result)),
                    SignalIntakeOutcome.DuplicateSignal => Results.Ok(
                        ToResponse("DUPLICATE_SIGNAL_IGNORED", result)),
                    SignalIntakeOutcome.Invalid => Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["signal"] = result.Errors.ToArray(),
                        }),
                    _ => Results.Problem(
                        title: "Signal processing failed",
                        detail: result.Error,
                        statusCode: StatusCodes.Status500InternalServerError),
                };
            });

        endpoints.MapGet(
            "/api/v1/signals",
            async (
                ISignalStore signalStore,
                int? limit,
                CancellationToken cancellationToken) =>
            {
                var signals = await signalStore.GetLatestAsync(
                    limit ?? 50,
                    cancellationToken);

                return Results.Ok(new
                {
                    signals,
                    count = signals.Count,
                });
            });

        return endpoints;
    }

    private static object ToResponse(string status, SignalIntakeResult result) => new
    {
        status,
        messageId = result.MessageId,
        signalUid = result.SignalUid,
        signalId = result.SignalId,
    };
}
