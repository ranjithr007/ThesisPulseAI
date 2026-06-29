using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public static class SignalEndpoints
{
    public static IEndpointRouteBuilder MapSignalEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        MapIntake(endpoints);
        MapStatusTransitions(endpoints);
        MapQueries(endpoints);
        return endpoints;
    }

    private static void MapIntake(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/v1/signals/intake",
            async (
                EventEnvelope<SignalGeneratedV1> envelope,
                SignalIntakeCoordinator coordinator,
                ISignalStatusStore statusStore,
                ISignalStreamPublisher streamPublisher,
                CancellationToken cancellationToken) =>
            {
                var result = await coordinator.ProcessAsync(
                    envelope,
                    cancellationToken);

                if (result.Outcome == SignalIntakeOutcome.Invalid)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["signal"] = result.Errors.ToArray(),
                    });
                }

                if (result.Outcome == SignalIntakeOutcome.Failed)
                {
                    return Results.Problem(
                        title: "Signal processing failed",
                        detail: result.Error,
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                if (result.Outcome == SignalIntakeOutcome.DuplicateMessage)
                {
                    return Results.Ok(ToIntakeResponse(
                        "DUPLICATE_MESSAGE_IGNORED",
                        result,
                        publication: null));
                }

                if (result.Outcome == SignalIntakeOutcome.DuplicateSignal)
                {
                    return Results.Ok(ToIntakeResponse(
                        "DUPLICATE_SIGNAL_IGNORED",
                        result,
                        publication: null));
                }

                var publication = await PublishCurrentAsync(
                    statusStore,
                    streamPublisher,
                    result.SignalUid,
                    statusSequence: 0,
                    envelope.Metadata.OccurredAtUtc,
                    envelope.Metadata.CorrelationId,
                    cancellationToken);

                return Results.Accepted(
                    $"/api/v1/signals/{result.SignalUid}",
                    ToIntakeResponse("ACCEPTED", result, publication));
            });
    }

    private static void MapStatusTransitions(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/v1/signals/{signalUid:guid}/status",
            async (
                Guid signalUid,
                SignalStatusTransitionV1 transition,
                ISignalStatusStore statusStore,
                ISignalStreamPublisher streamPublisher,
                CancellationToken cancellationToken) =>
            {
                var result = await statusStore.TransitionStatusAsync(
                    signalUid,
                    transition,
                    cancellationToken);

                if (result.Outcome == SignalTransitionOutcome.NotFound)
                {
                    return Results.NotFound(ToTransitionResponse(
                        "SIGNAL_NOT_FOUND",
                        result,
                        publication: null));
                }

                if (result.Outcome == SignalTransitionOutcome.Rejected)
                {
                    return Results.Conflict(ToTransitionResponse(
                        "TRANSITION_REJECTED",
                        result,
                        publication: null));
                }

                if (result.Outcome == SignalTransitionOutcome.Duplicate)
                {
                    return Results.Ok(ToTransitionResponse(
                        "DUPLICATE_TRANSITION_IGNORED",
                        result,
                        publication: null));
                }

                var publication = await PublishCurrentAsync(
                    statusStore,
                    streamPublisher,
                    signalUid,
                    result.EventSequence ?? 0,
                    transition.OccurredAtUtc,
                    transition.CorrelationId,
                    cancellationToken);

                return Results.Ok(ToTransitionResponse(
                    "TRANSITION_APPLIED",
                    result,
                    publication));
            });
    }

    private static void MapQueries(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/signals/{signalUid:guid}",
            async (
                Guid signalUid,
                ISignalStatusStore statusStore,
                CancellationToken cancellationToken) =>
            {
                var signal = await statusStore.GetAsync(signalUid, cancellationToken);
                return signal is null ? Results.NotFound() : Results.Ok(signal);
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
    }

    private static async Task<SignalStreamPublishResult> PublishCurrentAsync(
        ISignalStatusStore statusStore,
        ISignalStreamPublisher streamPublisher,
        Guid signalUid,
        int statusSequence,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var signal = await statusStore.GetAsync(signalUid, cancellationToken);
        if (signal is null)
        {
            return new SignalStreamPublishResult(
                SignalStreamPublishOutcome.Failed,
                "Persisted signal could not be loaded for publication.");
        }

        return await streamPublisher.PublishAsync(
            signal,
            statusSequence,
            occurredAtUtc,
            correlationId,
            cancellationToken);
    }

    private static object ToIntakeResponse(
        string status,
        SignalIntakeResult result,
        SignalStreamPublishResult? publication) => new
        {
            status,
            messageId = result.MessageId,
            signalUid = result.SignalUid,
            signalId = result.SignalId,
            streamPublication = publication?.Outcome.ToString().ToUpperInvariant(),
            streamError = publication?.Error,
        };

    private static object ToTransitionResponse(
        string status,
        SignalTransitionResult result,
        SignalStreamPublishResult? publication) => new
        {
            status,
            transitionUid = result.TransitionUid,
            signalUid = result.SignalUid,
            signalId = result.SignalId,
            previousStatus = result.PreviousStatus,
            currentStatus = result.CurrentStatus,
            eventSequence = result.EventSequence,
            reason = result.Reason,
            streamPublication = publication?.Outcome.ToString().ToUpperInvariant(),
            streamError = publication?.Error,
        };
}
