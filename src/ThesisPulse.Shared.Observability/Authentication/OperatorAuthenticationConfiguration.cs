using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed record ResolvedOperatorAuthentication(
    string Mode,
    string Issuer,
    string Audience,
    string? Authority,
    bool RequireHttpsMetadata,
    TimeSpan TokenLifetime,
    string? LocalUsername,
    string? LocalPassword,
    string? LocalDisplayName,
    byte[]? LocalSigningKey,
    IReadOnlyList<string> LocalPermissions,
    string? ServiceAccessToken,
    IReadOnlySet<string> InternalServiceHosts);

public static class OperatorAuthenticationConfiguration
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static ResolvedOperatorAuthentication Resolve(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration
            .GetSection(OperatorAuthenticationOptions.SectionName)
            .Get<OperatorAuthenticationOptions>()
            ?? new OperatorAuthenticationOptions();

        var mode = options.Mode.Trim();
        if (Comparer.Equals(mode, OperatorAuthenticationConstants.LocalMode))
        {
            return ResolveLocal(options, configuration, environment);
        }

        if (Comparer.Equals(mode, OperatorAuthenticationConstants.ExternalMode))
        {
            return ResolveExternal(options);
        }

        throw new InvalidOperationException(
            $"Authentication:Mode must be '{OperatorAuthenticationConstants.LocalMode}' or " +
            $"'{OperatorAuthenticationConstants.ExternalMode}'. Authentication cannot be disabled.");
    }

    private static ResolvedOperatorAuthentication ResolveLocal(
        OperatorAuthenticationOptions options,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var platformEnvironment = configuration["Platform:Environment"]?.Trim();
        if (!environment.IsDevelopment() ||
            !string.Equals(platformEnvironment, "PAPER", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LocalDevelopment authentication is permitted only when ASPNETCORE_ENVIRONMENT is " +
                "Development and Platform:Environment is PAPER.");
        }

        var issuer = Require(options.Issuer, "Authentication:Issuer");
        var audience = Require(options.Audience, "Authentication:Audience");
        var username = Require(options.Local.Username, "Authentication:Local:Username");
        var password = Require(options.Local.Password, "Authentication:Local:Password");
        var displayName = string.IsNullOrWhiteSpace(options.Local.DisplayName)
            ? username
            : options.Local.DisplayName.Trim();

        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(
                Require(options.Local.SigningKeyBase64, "Authentication:Local:SigningKeyBase64"));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Authentication:Local:SigningKeyBase64 must be valid Base64.",
                exception);
        }

        if (signingKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Authentication:Local:SigningKeyBase64 must decode to at least 32 bytes.");
        }

        var permissions = NormalizePermissions(options.Local.Permissions);
        if (!permissions.Contains(
                OperatorAuthenticationConstants.ReadPermission,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Authentication:Local:Permissions must include " +
                $"'{OperatorAuthenticationConstants.ReadPermission}'.");
        }

        return new ResolvedOperatorAuthentication(
            Mode: OperatorAuthenticationConstants.LocalMode,
            Issuer: issuer,
            Audience: audience,
            Authority: null,
            RequireHttpsMetadata: false,
            TokenLifetime: ResolveLifetime(options.TokenLifetimeMinutes),
            LocalUsername: username,
            LocalPassword: password,
            LocalDisplayName: displayName,
            LocalSigningKey: signingKey,
            LocalPermissions: permissions,
            ServiceAccessToken: null,
            InternalServiceHosts: NormalizeHosts(options.InternalServiceHosts, includeLoopback: true));
    }

    private static ResolvedOperatorAuthentication ResolveExternal(
        OperatorAuthenticationOptions options)
    {
        var authority = Require(options.Authority, "Authentication:Authority");
        var audience = Require(options.Audience, "Authentication:Audience");

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            throw new InvalidOperationException(
                "Authentication:Authority must be an absolute URI.");
        }

        if (options.RequireHttpsMetadata &&
            !string.Equals(authorityUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Authentication:Authority must use HTTPS when RequireHttpsMetadata is true.");
        }

        return new ResolvedOperatorAuthentication(
            Mode: OperatorAuthenticationConstants.ExternalMode,
            Issuer: options.Issuer.Trim(),
            Audience: audience,
            Authority: authority,
            RequireHttpsMetadata: options.RequireHttpsMetadata,
            TokenLifetime: ResolveLifetime(options.TokenLifetimeMinutes),
            LocalUsername: null,
            LocalPassword: null,
            LocalDisplayName: null,
            LocalSigningKey: null,
            LocalPermissions: Array.Empty<string>(),
            ServiceAccessToken: string.IsNullOrWhiteSpace(options.ServiceAccessToken)
                ? null
                : options.ServiceAccessToken.Trim(),
            InternalServiceHosts: NormalizeHosts(options.InternalServiceHosts, includeLoopback: false));
    }

    private static TimeSpan ResolveLifetime(int minutes)
    {
        if (minutes is < 5 or > 120)
        {
            throw new InvalidOperationException(
                "Authentication:TokenLifetimeMinutes must be between 5 and 120.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string Require(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required.");
        }

        return value.Trim();
    }

    private static IReadOnlyList<string> NormalizePermissions(IEnumerable<string>? permissions)
    {
        return (permissions ?? Array.Empty<string>())
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlySet<string> NormalizeHosts(
        IEnumerable<string>? hosts,
        bool includeLoopback)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeLoopback)
        {
            normalized.Add("localhost");
            normalized.Add("127.0.0.1");
            normalized.Add("::1");
        }

        foreach (var host in hosts ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                normalized.Add(host.Trim());
            }
        }

        return normalized;
    }
}
