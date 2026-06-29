namespace ThesisPulse.Shared.Infrastructure.Messaging;

public enum InboxReceiptStatus
{
    Processing = 0,
    Processed = 1,
    Failed = 2,
}

public sealed record InboxReceipt(
    Guid MessageId,
    string Consumer,
    InboxReceiptStatus Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ProcessedAtUtc,
    string? LastError);
