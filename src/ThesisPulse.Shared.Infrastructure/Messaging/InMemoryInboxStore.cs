using System.Collections.Concurrent;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<(Guid MessageId, string Consumer), InboxReceipt>
        _receipts = new();

    public Task<bool> TryBeginProcessingAsync(
        InboxMessage message,
        string consumer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        if (message.ReceivedAtUtc < message.Metadata.OccurredAtUtc)
        {
            throw new ArgumentException(
                "Inbox received time cannot be earlier than the message occurrence time.",
                nameof(message));
        }

        var receipt = new InboxReceipt(
            MessageId: message.Metadata.MessageId,
            Consumer: consumer,
            Status: InboxReceiptStatus.Processing,
            ReceivedAtUtc: message.ReceivedAtUtc,
            ProcessedAtUtc: null,
            LastError: null);

        return Task.FromResult(
            _receipts.TryAdd((message.Metadata.MessageId, consumer), receipt));
    }

    public Task MarkProcessedAsync(
        Guid messageId,
        string consumer,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        var key = (messageId, consumer);
        var current = GetRequired(key);

        _receipts[key] = current with
        {
            Status = InboxReceiptStatus.Processed,
            ProcessedAtUtc = processedAtUtc,
            LastError = null,
        };

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(
        Guid messageId,
        string consumer,
        string error,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var key = (messageId, consumer);
        var current = GetRequired(key);

        _receipts[key] = current with
        {
            Status = InboxReceiptStatus.Failed,
            LastError = error,
        };

        return Task.CompletedTask;
    }

    private InboxReceipt GetRequired((Guid MessageId, string Consumer) key)
    {
        if (_receipts.TryGetValue(key, out var receipt))
        {
            return receipt;
        }

        throw new KeyNotFoundException(
            $"Inbox receipt for message '{key.MessageId}' and consumer " +
            $"'{key.Consumer}' was not found.");
    }
}
