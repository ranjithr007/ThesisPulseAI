using System.Collections.Concurrent;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<(Guid MessageId, string Consumer), InboxReceipt>
        _receipts = new();

    public Task<bool> TryBeginProcessingAsync(
        Guid messageId,
        string consumer,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        var receipt = new InboxReceipt(
            MessageId: messageId,
            Consumer: consumer,
            Status: InboxReceiptStatus.Processing,
            ReceivedAtUtc: receivedAtUtc,
            ProcessedAtUtc: null,
            LastError: null);

        return Task.FromResult(_receipts.TryAdd((messageId, consumer), receipt));
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
