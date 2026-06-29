using System.Text.Json;
using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public enum InboxProcessingOutcome
{
    Processed = 0,
    Duplicate = 1,
    Failed = 2,
}

public sealed record InboxProcessingResult(
    InboxProcessingOutcome Outcome,
    Guid MessageId,
    string? Error);

public sealed class InboxMessageProcessor(
    IInboxStore inboxStore,
    IClock clock)
{
    public async Task<InboxProcessingResult> ProcessAsync<TPayload>(
        EventEnvelope<TPayload> envelope,
        string consumer,
        Func<TPayload, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentNullException.ThrowIfNull(handler);

        var message = new InboxMessage(
            Metadata: envelope.Metadata,
            PayloadJson: JsonSerializer.Serialize(envelope.Payload),
            ReceivedAtUtc: clock.UtcNow);

        var acquired = await inboxStore.TryBeginProcessingAsync(
            message,
            consumer,
            cancellationToken);

        if (!acquired)
        {
            return new InboxProcessingResult(
                InboxProcessingOutcome.Duplicate,
                envelope.Metadata.MessageId,
                Error: null);
        }

        try
        {
            await handler(envelope.Payload, cancellationToken);
            await inboxStore.MarkProcessedAsync(
                envelope.Metadata.MessageId,
                consumer,
                clock.UtcNow,
                cancellationToken);

            return new InboxProcessingResult(
                InboxProcessingOutcome.Processed,
                envelope.Metadata.MessageId,
                Error: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await inboxStore.MarkFailedAsync(
                envelope.Metadata.MessageId,
                consumer,
                exception.Message,
                cancellationToken);

            return new InboxProcessingResult(
                InboxProcessingOutcome.Failed,
                envelope.Metadata.MessageId,
                exception.Message);
        }
    }
}
