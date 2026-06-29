using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public enum InboxReceiptStatus
{
    Received = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
    DeadLetter = 4,
}

public sealed record InboxMessage(
    MessageMetadata Metadata,
    string PayloadJson,
    DateTimeOffset ReceivedAtUtc,
    InboxReceiptStatus Status = InboxReceiptStatus.Received,
    int AttemptCount = 0,
    DateTimeOffset? ProcessedAtUtc = null,
    string? LastError = null);

public sealed record InboxReceipt(
    Guid MessageId,
    string Consumer,
    InboxReceiptStatus Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ProcessedAtUtc,
    string? LastError);
