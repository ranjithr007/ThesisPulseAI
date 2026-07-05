using System.Security.Claims;

namespace ThesisPulse.Shared.Observability.Authentication;

public static class OperatorAuthorization
{
    public static bool HasReadPermission(ClaimsPrincipal principal) =>
        HasPermission(principal, OperatorAuthenticationConstants.ReadPermission) ||
        HasPermission(principal, OperatorAuthenticationConstants.OperatePermission) ||
        HasPermission(principal, OperatorAuthenticationConstants.AdminPermission);

    public static bool HasOperatePermission(ClaimsPrincipal principal) =>
        HasPermission(principal, OperatorAuthenticationConstants.OperatePermission) ||
        HasPermission(principal, OperatorAuthenticationConstants.AdminPermission);

    public static bool HasAdminPermission(ClaimsPrincipal principal) =>
        HasPermission(principal, OperatorAuthenticationConstants.AdminPermission);

    public static IReadOnlyList<string> GetPermissions(ClaimsPrincipal principal)
    {
        var explicitPermissions = principal.FindAll(OperatorAuthenticationConstants.PermissionClaim)
            .Select(claim => claim.Value);

        var scopePermissions = principal.FindAll(OperatorAuthenticationConstants.ScopeClaim)
            .SelectMany(claim => claim.Value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return explicitPermissions
            .Concat(scopePermissions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasPermission(ClaimsPrincipal principal, string permission) =>
        GetPermissions(principal).Contains(permission, StringComparer.Ordinal);
}
