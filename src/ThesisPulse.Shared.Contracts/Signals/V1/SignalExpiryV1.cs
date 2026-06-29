namespace ThesisPulse.Shared.Contracts.Signals.V1;

public sealed record ExpireDueSignalsRequestV1(
    DateTimeOffset AsOfUtc,
    int MaximumCount,
    string SourceService,
    string SourceVersion,
    string CorrelationId);

public sealed record ExpiredSignalV1(
    Guid TransitionUid,
    Guid SignalUid,
    long? SignalId,
    string PreviousStatus,
    string CurrentStatus,
    int EventSequence);

public sealed record ExpireDueSignalsResultV1(
    DateTimeOffset AsOfUtc,
    int Selected,
    int Expired,
    IReadOnlyCollection<ExpiredSignalV1> Signals);
