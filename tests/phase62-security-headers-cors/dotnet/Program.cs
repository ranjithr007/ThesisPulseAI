using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ThesisPulse.Shared.Observability.Security;

var tests = new (string Name, Action Run)[]
{
    ("Default security headers are emitted", DefaultSecurityHeadersAreEmitted),
    ("HSTS is disabled by default for local HTTP", HstsIsDisabledByDefaultForLocalHttp),
    ("HSTS requires explicit non local environment", HstsRequiresExplicitNonLocalEnvironment),
    ("HSTS emits only on HTTPS when enabled", HstsEmitsOnlyOnHttpsWhenEnabled),
    ("CORS defaults to local frontend origin", CorsDefaultsToLocalFrontendOrigin),
    ("CORS accepts explicit absolute origins", CorsAcceptsExplicitAbsoluteOrigins),
    ("CORS rejects wildcard origins", CorsRejectsWildcardOrigins),
    ("CORS rejects duplicate origins", CorsRejectsDuplicateOrigins),
    ("CORS rejects path query fragment and user info", CorsRejectsPathQueryFragmentAndUserInfo),
    ("CORS rejects unsupported schemes", CorsRejectsUnsupportedSchemes),
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

static void DefaultSecurityHeadersAreEmitted()
{
    var headers = new HeaderDictionary();
    var middleware = MiddlewareFor(LocalConfiguration(), Environments.Development);

    middleware.Apply(headers, requestIsHttps: false);

    AssertEqual("nosniff", headers[SecurityHeadersMiddleware.ContentTypeOptions].ToString());
    AssertEqual("no-referrer", headers[SecurityHeadersMiddleware.ReferrerPolicy].ToString());
    AssertEqual("DENY", headers[SecurityHeadersMiddleware.FrameOptions].ToString());
    AssertEqual("same-origin", headers[SecurityHeadersMiddleware.CrossOriginOpenerPolicy].ToString());
    AssertEqual("same-site", headers[SecurityHeadersMiddleware.CrossOriginResourcePolicy].ToString());
    AssertEqual(SecurityHeadersMiddleware.PermissionsPolicyValue,
        headers[SecurityHeadersMiddleware.PermissionsPolicy].ToString());
    AssertTrue(!headers.ContainsKey(SecurityHeadersMiddleware.StrictTransportSecurity),
        "HSTS must not be emitted by default for local HTTP.");
}

static void HstsIsDisabledByDefaultForLocalHttp()
{
    var options = SecurityHeadersOptions.Resolve(
        LocalConfiguration(),
        EnvironmentFor(Environments.Development));
    AssertEqual(false, options.EnableStrictTransportSecurity);
}

static void HstsRequiresExplicitNonLocalEnvironment()
{
    var localHsts = new Dictionary<string, string?>
    {
        ["Platform:Environment"] = "PAPER",
        ["SecurityHeaders:EnableStrictTransportSecurity"] = "true",
    };

    var exception = AssertThrows<InvalidOperationException>(() =>
        SecurityHeadersOptions.Resolve(
            BuildConfiguration(localHsts),
            EnvironmentFor(Environments.Development)));
    AssertContains(exception.Message, "Development/PAPER");

    var paperProduction = new Dictionary<string, string?>
    {
        ["Platform:Environment"] = "PAPER",
        ["SecurityHeaders:EnableStrictTransportSecurity"] = "true",
    };

    var paperException = AssertThrows<InvalidOperationException>(() =>
        SecurityHeadersOptions.Resolve(
            BuildConfiguration(paperProduction),
            EnvironmentFor(Environments.Production)));
    AssertContains(paperException.Message, "Development/PAPER");
}

static void HstsEmitsOnlyOnHttpsWhenEnabled()
{
    var configuration = BuildConfiguration(new Dictionary<string, string?>
    {
        ["Platform:Environment"] = "LIVE",
        ["SecurityHeaders:EnableStrictTransportSecurity"] = "true",
        ["SecurityHeaders:StrictTransportSecurityMaxAgeSeconds"] = "31536000",
        ["SecurityHeaders:IncludeSubDomains"] = "true",
        ["SecurityHeaders:Preload"] = "true",
    });

    var middleware = MiddlewareFor(configuration, Environments.Production);

    var httpHeaders = new HeaderDictionary();
    middleware.Apply(httpHeaders, requestIsHttps: false);
    AssertTrue(!httpHeaders.ContainsKey(SecurityHeadersMiddleware.StrictTransportSecurity),
        "HSTS must not be emitted on plain HTTP responses.");

    var httpsHeaders = new HeaderDictionary();
    middleware.Apply(httpsHeaders, requestIsHttps: true);
    AssertEqual("max-age=31536000; includeSubDomains; preload",
        httpsHeaders[SecurityHeadersMiddleware.StrictTransportSecurity].ToString());
}

static void CorsDefaultsToLocalFrontendOrigin()
{
    var origins = CorsOriginValidator.ResolveAllowedOrigins(LocalConfiguration());
    AssertEqual(1, origins.Length);
    AssertEqual(CorsOriginValidator.DefaultLocalFrontendOrigin, origins[0]);
}

static void CorsAcceptsExplicitAbsoluteOrigins()
{
    var origins = CorsOriginValidator.ResolveAllowedOrigins(BuildConfiguration(new Dictionary<string, string?>
    {
        ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
        ["Cors:AllowedOrigins:1"] = "https://operator.thesispulse.local",
    }));

    AssertEqual(2, origins.Length);
    AssertEqual("https://operator.thesispulse.local", origins[1]);
}

static void CorsRejectsWildcardOrigins()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://*.example.com" }));
    AssertContains(exception.Message, "wildcard");
}

static void CorsRejectsDuplicateOrigins()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://example.com", "https://EXAMPLE.com" }));
    AssertContains(exception.Message, "duplicate");
}

static void CorsRejectsPathQueryFragmentAndUserInfo()
{
    AssertContains(AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://example.com/app" })).Message, "path");
    AssertContains(AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://example.com?token=secret" })).Message, "query");
    AssertContains(AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://example.com#fragment" })).Message, "fragments");
    AssertContains(AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "https://user@example.com" })).Message, "user");
}

static void CorsRejectsUnsupportedSchemes()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        CorsOriginValidator.Validate(new[] { "file:///tmp/index.html" }));
    AssertContains(exception.Message, "http or https");
}

static SecurityHeadersMiddleware MiddlewareFor(IConfiguration configuration, string environmentName)
{
    var options = SecurityHeadersOptions.Resolve(configuration, EnvironmentFor(environmentName));
    return new SecurityHeadersMiddleware(_ => Task.CompletedTask, options);
}

static IConfiguration LocalConfiguration() => BuildConfiguration(new Dictionary<string, string?>
{
    ["Platform:Environment"] = "PAPER",
});

static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

static TestHostEnvironment EnvironmentFor(string environmentName) => new()
{
    ApplicationName = "ThesisPulse.SecurityHeadersCors.Tests",
    EnvironmentName = environmentName,
    ContentRootPath = Directory.GetCurrentDirectory(),
    ContentRootFileProvider = new NullFileProvider(),
};

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

sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
