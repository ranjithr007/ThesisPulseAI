using System.Collections.Concurrent;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();

    public Task AddAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);

        if (message.Status != OutboxMessageStatus.Pending)
        {
            throw new ArgumentException(
                "New outbox messages must start in Pending status.",
                nameof(message));
        }

        if (!_messages.TryAdd(message.Metadata.MessageId, message))
        {
            throw new InvalidOperationException(
                $"Outbox message '{message.Metadata.MessageId}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                "Maximum count must be greater than zero.");
        }

        IReadOnlyCollection<OutboxMessage> messages = _messages.Values
            .Where(message => message.Status is
                OutboxMessageStatus.Pending or OutboxMessageStatus.Failed)
            .OrderBy(message => message.Metadata.OccurredAtUtc)
            .Take(maximumCount)
            .ToArray();

        return Task.FromResult(messages);
    }

    public Task MarkPublishedAsync(
        Guid messageId,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetRequired(messageId);

        if (current.Status is OutboxMessageStatus.Published or OutboxMessageStatus.DeadLetter)
        {
            throw new InvalidOperationException(
                $"Outbox message '{messageId}' is already terminal.");
        }

        _messages[messageId] = current with
        {
            Status = OutboxMessageStatus.Published,
            AttemptCount = current.AttemptCount + 1,
            PublishedAtUtc = publishedAtUtc,
            LastError = null,
        };

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var current = GetRequired(messageId);

        if (current.Status is OutboxMessageStatus.Published or OutboxMessageStatus.DeadLetter)
        {
            throw new InvalidOperationException(
                $"Outbox message '{messageId}' is already terminal.");
        }

        _messages[messageId] = current with
        {
            Status = OutboxMessageStatus.Failed,
            AttemptCount = current.AttemptCount + 1,
            LastError = error,
        };

        return Task.CompletedTask;
    }

    private OutboxMessage GetRequired(Guid messageId)
    {
        if (_messages.TryGetValue(messageId, out var message))
        {
            return message;
        }

        throw new KeyNotFoundException(
            $"Outbox message '{messageId}' was not found.");
    }
}
