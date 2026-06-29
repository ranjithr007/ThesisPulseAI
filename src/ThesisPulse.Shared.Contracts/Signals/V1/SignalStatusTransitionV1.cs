namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalStatusV1
{
    public const string Candidate = "CANDIDATE";
    public const string Validated = "VALIDATED";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
    public const string Superseded = "SUPERSEDED";
    public const string Consumed = "CONSUMED";

    public static readonly IReadOnlySet<string> Values =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Candidate,
            Validated,
            Rejected,
            Expired,
            Superseded,
            Consumed,
        };
}

public sealed record SignalStatusTransitionV1(
    Guid TransitionUid,
    string TargetStatus,
    IReadOnlyCollection<string> ReasonCodes,
    DateTimeOffset OccurredAtUtc,
    string SourceService,
    string SourceVersion,
    string CorrelationId,
    string? CausationId,
    Guid? RelatedSignalUid,
    IReadOnlyDictionary<string, string>? Metadata);
