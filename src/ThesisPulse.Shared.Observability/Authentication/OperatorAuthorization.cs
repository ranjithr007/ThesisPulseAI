using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace ThesisPulse.Shared.Observability.Authentication;

public static class OperatorAuthorization
{
    public static bool CanAccessRequest(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            return context.User.Identity?.IsAuthenticated == true &&
                HasOperatePermission(context.User);
        }

        if (HttpMethods.IsOptions(httpContext.Request.Method))
        {
            return true;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return HttpMethods.IsGet(httpContext.Request.Method) ||
               HttpMethods.IsHead(httpContext.Request.Method)
            ? HasReadPermission(context.User)
            : HasOperatePermission(context.User);
    }

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
