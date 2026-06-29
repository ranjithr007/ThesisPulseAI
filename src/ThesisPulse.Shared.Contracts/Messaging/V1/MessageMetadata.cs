namespace ThesisPulse.Shared.Contracts.Messaging.V1;

public sealed record MessageMetadata(
    Guid MessageId,
    string EventType,
    string ContractVersion,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string? CausationId,
    string Producer,
    string ProducerVersion,
    string Environment,
    string ConfigurationVersion);
