using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Shared.Infrastructure.Time;
using ThesisPulse.Shared.Observability.Auditing;
using ThesisPulse.Shared.Observability.Authentication;
using ThesisPulse.Shared.Observability.Correlation;

var tests = new (string Name, Action Run)[]
{
    ("Classifier excludes platform observability", ClassifierExcludesPlatformObservability),
    ("Classifier excludes local token issuance", ClassifierExcludesLocalTokenIssuance),
    ("Classifier distinguishes preflight read and mutate", ClassifierDistinguishesRequestClasses),
    ("Outcome classification is safe", OutcomeClassificationIsSafe),
    ("Store retains bounded recent entries", StoreRetainsBoundedRecentEntries),
    ("Middleware captures allowed request without query string", MiddlewareCapturesAllowedRequestWithoutQueryString),
    ("Middleware captures unauthenticated denial", MiddlewareCapturesUnauthenticatedDenial),
    ("Middleware captures forbidden denial", MiddlewareCapturesForbiddenDenial),
    ("Middleware captures failure and rethrows", MiddlewareCapturesFailureAndRethrows),
    ("Audit options reject invalid retention", AuditOptionsRejectInvalidRetention),
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

static void ClassifierExcludesPlatformObservability()
{
    AssertEqual(
        OperatorAccessAuditContract.RequestClassPlatformObservability,
        OperatorAccessAuditClassifier.Classify("/health/ready", "GET"));
    AssertEqual(
        OperatorAccessAuditContract.RequestClassPlatformObservability,
        OperatorAccessAuditClassifier.Classify("/info", "GET"));
    AssertTrue(!OperatorAccessAuditClassifier.ShouldAudit("/health/live"),
        "Health endpoints must not be stored in the operator audit buffer.");
    AssertTrue(!OperatorAccessAuditClassifier.ShouldAudit("/info"),
        "Service info must not be stored in the operator audit buffer.");
}

static void ClassifierExcludesLocalTokenIssuance()
{
    AssertEqual(
        OperatorAccessAuditContract.RequestClassAuthentication,
        OperatorAccessAuditClassifier.Classify("/api/v1/auth/token", "POST"));
    AssertTrue(!OperatorAccessAuditClassifier.ShouldAudit("/api/v1/auth/token"),
        "Token issuance must not be stored because the request contains credentials.");
}

static void ClassifierDistinguishesRequestClasses()
{
    AssertEqual(
        OperatorAccessAuditContract.RequestClassPreflight,
        OperatorAccessAuditClassifier.Classify("/api/v1/signals", "OPTIONS"));
    AssertEqual(
        OperatorAccessAuditContract.RequestClassRead,
        OperatorAccessAuditClassifier.Classify("/api/v1/signals", "GET"));
    AssertEqual(
        OperatorAccessAuditContract.RequestClassMutate,
        OperatorAccessAuditClassifier.Classify("/internal/v1/paper-workflows/run", "POST"));
}

static void OutcomeClassificationIsSafe()
{
    AssertEqual(
        OperatorAccessAuditContract.OutcomeAllowed,
        OperatorAccessAuditClassifier.Outcome(StatusCodes.Status200OK, failed: false));
    AssertEqual(
        OperatorAccessAuditContract.OutcomeUnauthenticated,
        OperatorAccessAuditClassifier.Outcome(StatusCodes.Status401Unauthorized, failed: false));
    AssertEqual(
        OperatorAccessAuditContract.OutcomeForbidden,
        OperatorAccessAuditClassifier.Outcome(StatusCodes.Status403Forbidden, failed: false));
    AssertEqual(
        OperatorAccessAuditContract.OutcomeFailed,
        OperatorAccessAuditClassifier.Outcome(StatusCodes.Status500InternalServerError, failed: false));
    AssertEqual(
        OperatorAccessAuditContract.OutcomeFailed,
        OperatorAccessAuditClassifier.Outcome(StatusCodes.Status200OK, failed: true));
}

static void StoreRetainsBoundedRecentEntries()
{
    var store = new InMemoryOperatorAccessAuditStore(new OperatorAccessAuditOptions
    {
        Capacity = 10,
        MaximumReadLimit = 3,
    });

    for (var index = 1; index <= 12; index++)
    {
        store.Record(NewEntry($"/api/v1/{index}"));
    }

    var recent = store.GetRecent(10);
    AssertEqual(3, recent.Count);
    AssertEqual("/api/v1/12", recent[0].Path);
    AssertEqual("/api/v1/11", recent[1].Path);
    AssertEqual("/api/v1/10", recent[2].Path);
}

static void MiddlewareCapturesAllowedRequestWithoutQueryString()
{
    var store = CreateStore();
    var context = NewContext("GET", "/api/v1/signals", StatusCodes.Status200OK);
    context.Request.QueryString = new QueryString("?token=REDACTED&password=REDACTED");
    context.User = PrincipalWith(
        subject: "local:operator",
        name: "Local PAPER Operator",
        OperatorAuthenticationConstants.ReadPermission);

    InvokeMiddleware(context, store, next: httpContext =>
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    });

    var entry = store.GetRecent(1).Single();
    AssertEqual("/api/v1/signals", entry.Path);
    AssertTrue(!entry.Path.Contains("token", StringComparison.OrdinalIgnoreCase),
        "Audit path must not contain query strings or token-like parameters.");
    AssertEqual("local:operator", entry.OperatorSubject);
    AssertEqual("Local PAPER Operator", entry.OperatorName);
    AssertEqual(OperatorAccessAuditContract.RequestClassRead, entry.RequestClass);
    AssertEqual(OperatorAccessAuditContract.OutcomeAllowed, entry.AuthorizationOutcome);
    AssertTrue(entry.Permissions.Contains(OperatorAuthenticationConstants.ReadPermission),
        "Expected permission evidence.");
    AssertEqual("phase61-correlation", entry.CorrelationId);
}

static void MiddlewareCapturesUnauthenticatedDenial()
{
    var store = CreateStore();
    var context = NewContext("GET", "/api/v1/signals", StatusCodes.Status401Unauthorized);

    InvokeMiddleware(context, store, next: httpContext =>
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    });

    var entry = store.GetRecent(1).Single();
    AssertEqual(false, entry.IsAuthenticated);
    AssertEqual("anonymous", entry.OperatorSubject);
    AssertEqual(OperatorAccessAuditContract.OutcomeUnauthenticated, entry.AuthorizationOutcome);
}

static void MiddlewareCapturesForbiddenDenial()
{
    var store = CreateStore();
    var context = NewContext("POST", "/api/v1/execution/commands", StatusCodes.Status403Forbidden);
    context.User = PrincipalWith(
        subject: "local:readonly",
        name: "Read Only",
        OperatorAuthenticationConstants.ReadPermission);

    InvokeMiddleware(context, store, next: httpContext =>
    {
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    });

    var entry = store.GetRecent(1).Single();
    AssertEqual(true, entry.IsAuthenticated);
    AssertEqual(OperatorAccessAuditContract.RequestClassMutate, entry.RequestClass);
    AssertEqual(OperatorAccessAuditContract.OutcomeForbidden, entry.AuthorizationOutcome);
}

static void MiddlewareCapturesFailureAndRethrows()
{
    var store = CreateStore();
    var context = NewContext("GET", "/api/v1/platform/health", StatusCodes.Status200OK);

    var exception = AssertThrows<InvalidOperationException>(() =>
        InvokeMiddleware(context, store, _ => throw new InvalidOperationException("boom")));
    AssertContains(exception.Message, "boom");

    var entry = store.GetRecent(1).Single();
    AssertEqual(StatusCodes.Status500InternalServerError, entry.StatusCode);
    AssertEqual(OperatorAccessAuditContract.OutcomeFailed, entry.AuthorizationOutcome);
}

static void AuditOptionsRejectInvalidRetention()
{
    var exception = AssertThrows<InvalidOperationException>(() =>
        new OperatorAccessAuditOptions { Capacity = 10, MaximumReadLimit = 11 }.Validate());
    AssertContains(exception.Message, "cannot exceed");
}

static IOperatorAccessAuditStore CreateStore() =>
    new InMemoryOperatorAccessAuditStore(new OperatorAccessAuditOptions
    {
        Capacity = 10,
        MaximumReadLimit = 10,
    });

static DefaultHttpContext NewContext(string method, string path, int statusCode)
{
    var context = new DefaultHttpContext();
    context.Request.Method = method;
    context.Request.Path = path;
    context.Response.StatusCode = statusCode;
    context.TraceIdentifier = "fallback-correlation";
    context.Items[CorrelationIdMiddleware.HeaderName] = "phase61-correlation";
    return context;
}

static void InvokeMiddleware(
    HttpContext context,
    IOperatorAccessAuditStore store,
    RequestDelegate next)
{
    var middleware = new OperatorAccessAuditMiddleware(
        next,
        store,
        new FixedClock(new DateTimeOffset(2026, 7, 6, 7, 15, 0, TimeSpan.Zero)),
        new TestHostEnvironment
        {
            ApplicationName = "ThesisPulse.OperatorAccessAudit.Tests",
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.GetCurrentDirectory(),
            ContentRootFileProvider = new NullFileProvider(),
        },
        NullLogger<OperatorAccessAuditMiddleware>.Instance);

    middleware.InvokeAsync(context).GetAwaiter().GetResult();
}

static ClaimsPrincipal PrincipalWith(string subject, string name, params string[] permissions)
{
    var claims = new List<Claim>
    {
        new("sub", subject),
        new("name", name),
    };
    claims.AddRange(permissions.Select(permission =>
        new Claim(OperatorAuthenticationConstants.PermissionClaim, permission)));

    return new ClaimsPrincipal(new ClaimsIdentity(
        claims,
        OperatorAuthenticationConstants.Scheme));
}

static OperatorAccessAuditEntry NewEntry(string path) => new(
    AuditUid: Guid.NewGuid(),
    ObservedAtUtc: DateTimeOffset.UtcNow,
    ServiceName: "TestService",
    Method: "GET",
    Path: path,
    StatusCode: StatusCodes.Status200OK,
    CorrelationId: Guid.NewGuid().ToString("D"),
    IsAuthenticated: true,
    OperatorSubject: "local:test",
    OperatorName: "Test Operator",
    Permissions: new[] { OperatorAuthenticationConstants.ReadPermission },
    RequestClass: OperatorAccessAuditContract.RequestClassRead,
    AuthorizationOutcome: OperatorAccessAuditContract.OutcomeAllowed);

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
