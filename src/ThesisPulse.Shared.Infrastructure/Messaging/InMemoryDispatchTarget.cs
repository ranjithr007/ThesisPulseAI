using System.Collections.Concurrent;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class InMemoryDispatchTarget : IDispatchTarget
{
    private readonly ConcurrentQueue<OutboxMessage> _publishedMessages = new();

    public IReadOnlyCollection<OutboxMessage> PublishedMessages =>
        _publishedMessages.ToArray();

    public Task SendAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        _publishedMessages.Enqueue(message);
        return Task.CompletedTask;
    }
}
