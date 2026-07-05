using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ThesisPulse.Shared.Infrastructure.Time;
using ThesisPulse.Shared.Observability.Authentication;

var tests = new (string Name, Action Run)[]
{
    ("Missing mode fails closed", MissingModeFailsClosed),
    ("Local mode rejects non-Development", LocalModeRejectsNonDevelopment),
    ("Local mode rejects non-PAPER", LocalModeRejectsNonPaper),
    ("Local mode requires strong signing key", LocalModeRequiresStrongSigningKey),
    ("Valid local configuration resolves", ValidLocalConfigurationResolves),
    ("Invalid credentials do not issue a token", InvalidCredentialsDoNotIssueToken),
    ("Valid credentials issue expected claims", ValidCredentialsIssueExpectedClaims),
    ("Read permission hierarchy is enforced", ReadPermissionHierarchyIsEnforced),
    ("Mutating requests require operate permission", MutatingRequestsRequireOperatePermission),
    ("Anonymous OPTIONS is permitted", AnonymousOptionsIsPermitted),
    ("Internal hosts are bounded", InternalHostsAreBounded),
};

var failed = false;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed = true;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failed ? 1 : 0;

static void MissingModeFailsClosed()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        OperatorAuthenticationConfiguration.Resolve(
            new ConfigurationBuilder().Build(),
            NewEnvironment(Environments.Development)));
    AssertContains(exception.Message, "Authentication:Mode");
}

static void LocalModeRejectsNonDevelopment()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        ResolveValidLocal(Environments.Production, "PAPER"));
    AssertContains(exception.Message, "Development");
}

static void LocalModeRejectsNonPaper()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        ResolveValidLocal(Environments.Development, "LIVE"));
    AssertContains(exception.Message, "PAPER");
}

static void LocalModeRequiresStrongSigningKey()
{
    var values = ValidLocalValues();
    values["Authentication:Local:SigningKeyBase64"] = Convert.ToBase64String(new byte[16]);
    var exception = AssertThrows<InvalidOperationException>(() =>
        OperatorAuthenticationConfiguration.Resolve(
            BuildConfiguration(values),
            NewEnvironment(Environments.Development)));
    AssertContains(exception.Message, "at least 32 bytes");
}

static void ValidLocalConfigurationResolves()
{
    var resolved = ResolveValidLocal(Environments.Development, "PAPER");
    AssertEqual(OperatorAuthenticationConstants.LocalMode, resolved.Mode);
    AssertEqual("operator", resolved.LocalUsername);
    AssertTrue(resolved.LocalSigningKey is { Length: 64 }, "Expected a 64-byte signing key.");
    AssertTrue(resolved.LocalPermissions.Contains(OperatorAuthenticationConstants.ReadPermission),
        "Expected read permission.");
}

static void InvalidCredentialsDoNotIssueToken()
{
    var service = CreateTokenService();
    var result = service.TryIssue(new LocalOperatorTokenRequest("operator", "wrong"));
    AssertTrue(result is null, "Invalid credentials must not issue a token.");
}

static void ValidCredentialsIssueExpectedClaims()
{
    var service = CreateTokenService();
    var result = service.TryIssue(new LocalOperatorTokenRequest("operator", "test-password"));
    AssertTrue(result is not null, "Valid local credentials should issue a token.");
    AssertEqual("Bearer", result!.TokenType);
    AssertEqual("Local PAPER Operator", result.Operator.DisplayName);

    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
    AssertEqual("ThesisPulse.LocalDevelopment", jwt.Issuer);
    AssertTrue(jwt.Audiences.Contains("ThesisPulse.Operator"), "Expected configured audience.");
    AssertTrue(jwt.Claims.Any(claim =>
            claim.Type == OperatorAuthenticationConstants.PermissionClaim &&
            claim.Value == OperatorAuthenticationConstants.ReadPermission),
        "Expected explicit read permission claim.");
    AssertTrue(jwt.ValidTo > DateTime.UtcNow, "Token must have a future expiry.");
}

static void ReadPermissionHierarchyIsEnforced()
{
    AssertTrue(OperatorAuthorization.HasReadPermission(PrincipalWith(
        OperatorAuthenticationConstants.ReadPermission)), "Read should permit read.");
    AssertTrue(OperatorAuthorization.HasReadPermission(PrincipalWith(
        OperatorAuthenticationConstants.OperatePermission)), "Operate should imply read.");
    AssertTrue(OperatorAuthorization.HasReadPermission(PrincipalWith(
        OperatorAuthenticationConstants.AdminPermission)), "Admin should imply read.");
    AssertTrue(!OperatorAuthorization.HasOperatePermission(PrincipalWith(
        OperatorAuthenticationConstants.ReadPermission)), "Read must not imply operate.");
}

static void MutatingRequestsRequireOperatePermission()
{
    var readOnly = ContextFor("POST", PrincipalWith(OperatorAuthenticationConstants.ReadPermission));
    var operatorContext = ContextFor("POST", PrincipalWith(
        OperatorAuthenticationConstants.OperatePermission));
    AssertTrue(!OperatorAuthorization.CanAccessRequest(readOnly),
        "Read-only identity must not mutate state.");
    AssertTrue(OperatorAuthorization.CanAccessRequest(operatorContext),
        "Operate identity should access a mutating request.");
}

static void AnonymousOptionsIsPermitted()
{
    var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
    var context = ContextFor("OPTIONS", anonymous);
    AssertTrue(OperatorAuthorization.CanAccessRequest(context),
        "Anonymous CORS preflight must be permitted.");
}

static void InternalHostsAreBounded()
{
    var resolved = ResolveValidLocal(Environments.Development, "PAPER");
    AssertTrue(resolved.InternalServiceHosts.Contains("localhost"),
        "Local mode should trust localhost.");
    AssertTrue(resolved.InternalServiceHosts.Contains("127.0.0.1"),
        "Local mode should trust IPv4 loopback.");
    AssertTrue(!resolved.InternalServiceHosts.Contains("api.upstox.com"),
        "External broker hosts must not receive internal service tokens.");
}

static ResolvedOperatorAuthentication ResolveValidLocal(string environmentName, string platform)
{
    var values = ValidLocalValues();
    values["Platform:Environment"] = platform;
    return OperatorAuthenticationConfiguration.Resolve(
        BuildConfiguration(values),
        NewEnvironment(environmentName));
}

static LocalOperatorTokenService CreateTokenService()
{
    return new LocalOperatorTokenService(
        BuildConfiguration(ValidLocalValues()),
        NewEnvironment(Environments.Development),
        new FixedClock(new DateTimeOffset(2026, 7, 5, 3, 30, 0, TimeSpan.Zero)));
}

static Dictionary<string, string?> ValidLocalValues() => new(StringComparer.OrdinalIgnoreCase)
{
    ["Platform:Environment"] = "PAPER",
    ["Authentication:Mode"] = OperatorAuthenticationConstants.LocalMode,
    ["Authentication:Issuer"] = "ThesisPulse.LocalDevelopment",
    ["Authentication:Audience"] = "ThesisPulse.Operator",
    ["Authentication:TokenLifetimeMinutes"] = "30",
    ["Authentication:Local:Username"] = "operator",
    ["Authentication:Local:Password"] = "test-password",
    ["Authentication:Local:DisplayName"] = "Local PAPER Operator",
    ["Authentication:Local:SigningKeyBase64"] = Convert.ToBase64String(
        Enumerable.Range(0, 64).Select(value => (byte)value).ToArray()),
    ["Authentication:Local:Permissions:0"] = OperatorAuthenticationConstants.ReadPermission,
};

static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

static TestHostEnvironment NewEnvironment(string environmentName) => new()
{
    ApplicationName = "ThesisPulse.Authentication.Tests",
    EnvironmentName = environmentName,
    ContentRootPath = Directory.GetCurrentDirectory(),
    ContentRootFileProvider = new NullFileProvider(),
};

static ClaimsPrincipal PrincipalWith(params string[] permissions)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, "test-operator"),
        new("name", "Test Operator"),
    };
    claims.AddRange(permissions.Select(permission =>
        new Claim(OperatorAuthenticationConstants.PermissionClaim, permission)));
    return new ClaimsPrincipal(new ClaimsIdentity(
        claims,
        OperatorAuthenticationConstants.Scheme));
}

static AuthorizationHandlerContext ContextFor(string method, ClaimsPrincipal principal)
{
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Method = method;
    return new AuthorizationHandlerContext(
        Array.Empty<IAuthorizationRequirement>(),
        principal,
        httpContext);
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertContains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }
}

sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
