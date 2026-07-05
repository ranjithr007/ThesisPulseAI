using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.Shared.Observability.Authentication;

public sealed record LocalOperatorTokenRequest(string Username, string Password);

public sealed record OperatorIdentityResponse(
    string Subject,
    string DisplayName,
    IReadOnlyList<string> Permissions);

public sealed record OperatorTokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc,
    OperatorIdentityResponse Operator);

public sealed class LocalOperatorTokenService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IClock _clock;

    public LocalOperatorTokenService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IClock clock)
    {
        _configuration = configuration;
        _environment = environment;
        _clock = clock;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                var resolved = OperatorAuthenticationConfiguration.Resolve(
                    _configuration,
                    _environment);
                return string.Equals(
                    resolved.Mode,
                    OperatorAuthenticationConstants.LocalMode,
                    StringComparison.Ordinal);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public OperatorTokenResponse? TryIssue(LocalOperatorTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = OperatorAuthenticationConfiguration.Resolve(
            _configuration,
            _environment);

        if (!string.Equals(
                resolved.Mode,
                OperatorAuthenticationConstants.LocalMode,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!FixedTimeEquals(request.Username, resolved.LocalUsername!) ||
            !FixedTimeEquals(request.Password, resolved.LocalPassword!))
        {
            return null;
        }

        var now = _clock.UtcNow;
        var expiresAt = now.Add(resolved.TokenLifetime);
        var subject = $"local:{resolved.LocalUsername}";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.UniqueName, resolved.LocalUsername!),
            new("name", resolved.LocalDisplayName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        };

        claims.AddRange(resolved.LocalPermissions.Select(permission =>
            new Claim(OperatorAuthenticationConstants.PermissionClaim, permission)));

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(resolved.LocalSigningKey!),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: resolved.Issuer,
            audience: resolved.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: signingCredentials);

        var encodedToken = new JwtSecurityTokenHandler().WriteToken(token);
        return new OperatorTokenResponse(
            AccessToken: encodedToken,
            TokenType: "Bearer",
            ExpiresAtUtc: expiresAt,
            Operator: new OperatorIdentityResponse(
                Subject: subject,
                DisplayName: resolved.LocalDisplayName!,
                Permissions: resolved.LocalPermissions));
    }

    private static bool FixedTimeEquals(string candidate, string expected)
    {
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? string.Empty));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
    }
}
