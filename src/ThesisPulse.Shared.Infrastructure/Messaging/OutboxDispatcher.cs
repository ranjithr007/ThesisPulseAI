namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed record OutboxDispatchResult(
    int Selected,
    int Published,
    int Failed);

public sealed class OutboxDispatcher(
    IOutboxStore outboxStore,
    IDispatchTarget dispatchTarget,
    Time.IClock clock)
{
    public async Task<OutboxDispatchResult> DispatchAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        var pendingMessages = await outboxStore.GetPendingAsync(
            maximumCount,
            cancellationToken);

        var published = 0;
        var failed = 0;

        foreach (var message in pendingMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await dispatchTarget.SendAsync(message, cancellationToken);
                await outboxStore.MarkPublishedAsync(
                    message.Metadata.MessageId,
                    clock.UtcNow,
                    cancellationToken);
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await outboxStore.MarkFailedAsync(
                    message.Metadata.MessageId,
                    exception.Message,
                    cancellationToken);
                failed++;
            }
        }

        return new OutboxDispatchResult(
            Selected: pendingMessages.Count,
            Published: published,
            Failed: failed);
    }
}
