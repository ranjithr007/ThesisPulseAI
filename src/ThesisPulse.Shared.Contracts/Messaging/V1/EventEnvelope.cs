namespace ThesisPulse.Shared.Contracts.Messaging.V1;

public sealed record EventEnvelope<TPayload>(
    MessageMetadata Metadata,
    TPayload Payload)
    where TPayload : class;
