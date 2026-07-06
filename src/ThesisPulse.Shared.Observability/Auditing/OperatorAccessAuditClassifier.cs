using Microsoft.AspNetCore.Http;

namespace ThesisPulse.Shared.Observability.Auditing;

public static class OperatorAccessAuditClassifier
{
    public static bool ShouldAudit(PathString path) =>
        Classify(path, method: null) is not
            OperatorAccessAuditContract.RequestClassPlatformObservability and not
            OperatorAccessAuditContract.RequestClassAuthentication;

    public static string Classify(PathString path, string? method)
    {
        var normalizedPath = path.Value ?? "/";
        if (normalizedPath.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, "/info", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorAccessAuditContract.RequestClassPlatformObservability;
        }

        if (string.Equals(
                normalizedPath,
                "/api/v1/auth/token",
                StringComparison.OrdinalIgnoreCase))
        {
            return OperatorAccessAuditContract.RequestClassAuthentication;
        }

        if (method is not null && HttpMethods.IsOptions(method))
        {
            return OperatorAccessAuditContract.RequestClassPreflight;
        }

        if (method is not null &&
            (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            return OperatorAccessAuditContract.RequestClassRead;
        }

        return OperatorAccessAuditContract.RequestClassMutate;
    }

    public static string Outcome(int statusCode, bool failed) =>
        failed || statusCode >= StatusCodes.Status500InternalServerError
            ? OperatorAccessAuditContract.OutcomeFailed
            : statusCode == StatusCodes.Status401Unauthorized
                ? OperatorAccessAuditContract.OutcomeUnauthenticated
                : statusCode == StatusCodes.Status403Forbidden
                    ? OperatorAccessAuditContract.OutcomeForbidden
                    : OperatorAccessAuditContract.OutcomeAllowed;
}
