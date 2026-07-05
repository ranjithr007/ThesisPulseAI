namespace ThesisPulse.Shared.Observability.Authentication;

public static class OperatorAuthenticationConstants
{
    public const string Scheme = "ThesisPulseBearer";

    public const string PermissionClaim = "thesispulse.permission";
    public const string ScopeClaim = "scope";

    public const string ReadPermission = "thesispulse.read";
    public const string OperatePermission = "thesispulse.operate";
    public const string AdminPermission = "thesispulse.admin";

    public const string ReadPolicy = "ThesisPulse.Read";
    public const string OperatePolicy = "ThesisPulse.Operate";
    public const string AdminPolicy = "ThesisPulse.Admin";

    public const string LocalMode = "LocalDevelopment";
    public const string ExternalMode = "ExternalJwt";
}
