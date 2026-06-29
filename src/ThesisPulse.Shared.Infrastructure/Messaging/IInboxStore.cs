namespace ThesisPulse.Shared.Infrastructure.Messaging;

public interface IInboxStore
{
    Task<bool> TryBeginProcessingAsync(
        InboxMessage message,
        string consumer,
        CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(
        Guid messageId,
        string consumer,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid messageId,
        string consumer,
        string error,
        CancellationToken cancellationToken = default);
}
