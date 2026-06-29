namespace ThesisPulse.Shared.Infrastructure.Messaging;

public interface IOutboxStore
{
    Task AddAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken = default);
}
