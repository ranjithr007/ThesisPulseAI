using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed class InternalServiceTokenProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private string? _cachedToken;
    private DateTimeOffset _cachedExpiresAtUtc;

    public InternalServiceTokenProvider(
        IConfiguration configuration,
        IHostEnvironment environment,
        IClock clock)
    {
        _configuration = configuration;
        _environment = environment;
        _clock = clock;
    }

    public string GetAccessToken()
    {
        var resolved = OperatorAuthenticationConfiguration.Resolve(
            _configuration,
            _environment);

        if (string.Equals(
                resolved.Mode,
                OperatorAuthenticationConstants.ExternalMode,
                StringComparison.Ordinal))
        {
            return resolved.ServiceAccessToken
                ?? throw new InvalidOperationException(
                    "Authentication:ServiceAccessToken is required for internal HTTP calls in ExternalJwt mode.");
        }

        lock (_gate)
        {
            var now = _clock.UtcNow;
            if (_cachedToken is not null && _cachedExpiresAtUtc > now.AddMinutes(1))
            {
                return _cachedToken;
            }

            var lifetime = resolved.TokenLifetime > TimeSpan.FromMinutes(30)
                ? TimeSpan.FromMinutes(30)
                : resolved.TokenLifetime;
            var expiresAt = now.Add(lifetime);
            var serviceName = string.IsNullOrWhiteSpace(_environment.ApplicationName)
                ? "ThesisPulse.Unknown.Service"
                : _environment.ApplicationName;
            var subject = $"service:{serviceName}";

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim("name", serviceName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                new Claim(
                    JwtRegisteredClaimNames.Iat,
                    now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ClaimValueTypes.Integer64),
                new Claim(
                    OperatorAuthenticationConstants.PermissionClaim,
                    OperatorAuthenticationConstants.ReadPermission),
                new Claim(
                    OperatorAuthenticationConstants.PermissionClaim,
                    OperatorAuthenticationConstants.OperatePermission),
            };

            var token = new JwtSecurityToken(
                issuer: resolved.Issuer,
                audience: resolved.Audience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: expiresAt.UtcDateTime,
                signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(resolved.LocalSigningKey!),
                    SecurityAlgorithms.HmacSha256));

            _cachedToken = new JwtSecurityTokenHandler().WriteToken(token);
            _cachedExpiresAtUtc = expiresAt;
            return _cachedToken;
        }
    }
}
