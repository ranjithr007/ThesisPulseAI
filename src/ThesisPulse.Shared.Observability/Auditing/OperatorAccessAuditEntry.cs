namespace ThesisPulse.Shared.Observability.Auditing;

public static class OperatorAccessAuditContract
{
    public const string ContractVersion = "1.0.0";

    public const string RequestClassRead = "READ";
    public const string RequestClassMutate = "MUTATE";
    public const string RequestClassPreflight = "PREFLIGHT";
    public const string RequestClassAuthentication = "AUTHENTICATION";
    public const string RequestClassPlatformObservability = "PLATFORM_OBSERVABILITY";

    public const string OutcomeAllowed = "ALLOWED";
    public const string OutcomeUnauthenticated = "UNAUTHENTICATED";
    public const string OutcomeForbidden = "FORBIDDEN";
    public const string OutcomeFailed = "FAILED";
}

public sealed record OperatorAccessAuditEntry(
    Guid AuditUid,
    DateTimeOffset ObservedAtUtc,
    string ServiceName,
    string Method,
    string Path,
    int StatusCode,
    string CorrelationId,
    bool IsAuthenticated,
    string OperatorSubject,
    string OperatorName,
    IReadOnlyList<string> Permissions,
    string RequestClass,
    string AuthorizationOutcome);

public sealed record OperatorAccessAuditReport(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    int Count,
    IReadOnlyList<OperatorAccessAuditEntry> Entries);
