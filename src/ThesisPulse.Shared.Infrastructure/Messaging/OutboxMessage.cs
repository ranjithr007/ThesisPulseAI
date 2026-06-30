using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public enum OutboxMessageStatus
{
    Pending = 0,
    Published = 1,
    Failed = 2,
    DeadLetter = 3,
}

public sealed record OutboxMessage(
    MessageMetadata Metadata,
    string PayloadJson,
    OutboxMessageStatus Status,
    int AttemptCount,
    DateTimeOffset? PublishedAtUtc,
    string? LastError,
    long StreamPosition = 0);
