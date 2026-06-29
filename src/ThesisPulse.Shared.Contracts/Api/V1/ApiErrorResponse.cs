namespace ThesisPulse.Shared.Contracts.Api.V1;

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string CorrelationId,
    DateTimeOffset TimestampUtc,
    IReadOnlyCollection<string> Details);
