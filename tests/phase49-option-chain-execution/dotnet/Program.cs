using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Signal.Service;

var failures = new List<string>();

await RunAsync("created output completes with replay-safe message identity", TestCreatedAsync, failures);
await RunAsync("duplicate output maps to duplicate", TestDuplicateAsync, failures);
await RunAsync("ineligible output maps to rejected", TestIneligibleAsync, failures);
await RunAsync("service unavailable maps to retry", TestRetryableAsync, failures);
await RunAsync("unprocessable response maps to rejected", TestUnprocessableAsync, failures);
await RunAsync("unauthorized response fails terminally", TestUnauthorizedAsync, failures);
await RunAsync("authority drift fails terminally", TestAuthorityDriftAsync, failures);
await RunAsync("missing snapshot lineage fails closed", TestMissingSnapshotAsync, failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine("Phase 4.9 option-chain execution acceptance failed:");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("Phase 4.9 option-chain execution acceptance passed.");
return 0;

static async Task RunAsync(
    string name,
    Func<Task> test,
    ICollection<string> failures)
{
    try
    {
        await test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static async Task TestCreatedAsync()
{
    var work = WorkItem();
    var transport = new CapturingHandler(_ => JsonResponse(
        HttpStatusCode.OK,
        ProcessingJson(work, "CREATED", includeOutput: true)));
    var handler = CreateHandler(work, transport);

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.Completed,
        "CREATED must complete the leased work item.");
    Assert(transport.LastInternalKey == "test-internal-key",
        "The internal authentication header must be sent.");
    using var document = JsonDocument.Parse(transport.LastBody ?? "{}");
    var root = document.RootElement;
    Assert(root.GetProperty("sourceMessageUid").GetGuid() == work.WorkUid,
        "The durable work UID must be used as the replay-safe source message UID.");
    Assert(root.GetProperty("snapshotUid").GetGuid() == work.SnapshotUid,
        "The source snapshot UID must be preserved.");
}

static async Task TestDuplicateAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            ProcessingJson(work, "DUPLICATE", includeOutput: false))));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.Duplicate,
        "DUPLICATE must complete through the duplicate terminal state.");
}

static async Task TestIneligibleAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            ProcessingJson(work, "IGNORED_INELIGIBLE", includeOutput: false))));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.Rejected,
        "IGNORED_INELIGIBLE must fail closed as rejected.");
}

static async Task TestRetryableAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            "{\"detail\":\"temporarily unavailable\"}")));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.RetryableFailure,
        "HTTP 503 must use the bounded retry path.");
}

static async Task TestUnprocessableAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.UnprocessableEntity,
            "{\"detail\":\"snapshot invalid\"}")));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.Rejected,
        "HTTP 422 must reject the canonical snapshot.");
}

static async Task TestUnauthorizedAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            "{\"detail\":\"Unauthorized\"}")));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.TerminalFailure,
        "Authentication failures must not be retried as data failures.");
}

static async Task TestAuthorityDriftAsync()
{
    var work = WorkItem();
    var handler = CreateHandler(
        work,
        new CapturingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            ProcessingJson(work, "CREATED", includeOutput: true, authorityDrift: true))));

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.TerminalFailure,
        "Authority-bearing intelligence output must fail terminally.");
    Assert(result.Reason == "PYTHON_AUTHORITY_DRIFT",
        "Authority drift must have an explicit reason code.");
}

static async Task TestMissingSnapshotAsync()
{
    var work = WorkItem();
    var options = WorkerOptions();
    var handler = new OptionChainPythonWorkHandler(
        new StaticSnapshotSource(null),
        new StaticHttpClientFactory(new CapturingHandler(_ =>
            throw new InvalidOperationException("HTTP must not be called"))),
        options,
        NullLogger<OptionChainPythonWorkHandler>.Instance);

    var result = await handler.HandleAsync(work);

    Assert(result.Outcome == OptionChainWorkExecutionOutcome.Rejected,
        "A missing point-in-time snapshot must be rejected before dispatch.");
}

static OptionChainPythonWorkHandler CreateHandler(
    OptionChainWorkItem work,
    CapturingHandler transport) =>
    new(
        new StaticSnapshotSource(Snapshot(work)),
        new StaticHttpClientFactory(transport),
        WorkerOptions(),
        NullLogger<OptionChainPythonWorkHandler>.Instance);

static OptionChainPythonWorkerOptions WorkerOptions() => new()
{
    Enabled = true,
    ServiceBaseUrl = "http://localhost:8000",
    InternalApiKey = "test-internal-key",
    RequestTimeoutSeconds = 30,
    PollIntervalSeconds = 5,
    BatchSize = 25,
};

static OptionChainWorkItem WorkItem()
{
    var cutoff = DateTimeOffset.Parse("2026-07-03T10:00:30Z");
    return new OptionChainWorkItem(
        Guid.Parse("49000000-0000-0000-0000-000000000001"),
        Guid.Parse("49000000-0000-0000-0000-000000000002"),
        "NSE_INDEX:NIFTY 50",
        cutoff,
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion,
        OptionChainWorkStatus.Leased,
        1,
        cutoff,
        "phase49-tests",
        cutoff.AddMinutes(2),
        null,
        cutoff,
        cutoff);
}

static OptionChainSnapshotDispatchV1 Snapshot(OptionChainWorkItem work) => new(
    work.WorkUid,
    work.SnapshotUid,
    work.InstrumentKey,
    new DateOnly(2026, 7, 9),
    work.WorkflowCutoffUtc.AddSeconds(-30),
    work.WorkflowCutoffUtc,
    25000m,
    "COMPLETE",
    "VALID",
    true,
    0,
    new[]
    {
        new OptionChainSnapshotDispatchEntryV1(
            Guid.Parse("49000000-0000-0000-0000-000000000003"),
            "NSE_FO|NIFTY26JUL25000CE",
            new DateOnly(2026, 7, 9),
            25000m,
            "CALL",
            120m,
            1000m,
            5000m,
            0.18m,
            0.5m,
            75m,
            "VALID",
            "upstox-greeks-v1"),
        new OptionChainSnapshotDispatchEntryV1(
            Guid.Parse("49000000-0000-0000-0000-000000000004"),
            "NSE_FO|NIFTY26JUL25000PE",
            new DateOnly(2026, 7, 9),
            25000m,
            "PUT",
            110m,
            900m,
            5200m,
            0.19m,
            -0.5m,
            75m,
            "VALID",
            "upstox-greeks-v1"),
    },
    "upstox-option-chain-v1");

static string ProcessingJson(
    OptionChainWorkItem work,
    string outcome,
    bool includeOutput,
    bool authorityDrift = false)
{
    object? output = null;
    if (includeOutput)
    {
        output = new
        {
            outputUid = Guid.Parse("49000000-0000-0000-0000-000000000010"),
            messageUid = Guid.Parse("49000000-0000-0000-0000-000000000011"),
            sourceSnapshotUids = new[] { work.SnapshotUid },
            underlyingInstrumentKey = work.InstrumentKey,
            asOfUtc = work.WorkflowCutoffUtc.AddSeconds(-30),
            generatedAtUtc = work.WorkflowCutoffUtc,
            engineCode = OptionChainIntelligenceContractV1.EngineCode,
            engineVersion = work.EngineVersion,
            policyVersion = work.PolicyVersion,
            direction = "LONG",
            score = 0.4m,
            confidence = 0.8m,
            expiryMetrics = new[]
            {
                new
                {
                    snapshotUid = work.SnapshotUid,
                    expiryDate = new DateOnly(2026, 7, 9),
                    underlyingPrice = 25000m,
                    callOpenInterest = 5000m,
                    putOpenInterest = 5200m,
                    pcrOpenInterest = (decimal?)1.04m,
                    callVolume = 1000m,
                    putVolume = 900m,
                    pcrVolume = (decimal?)0.9m,
                    callWalls = Array.Empty<object>(),
                    putWalls = Array.Empty<object>(),
                    oiFlows = Array.Empty<object>(),
                    maxPainStrike = (decimal?)25000m,
                    maxPainDistanceFraction = (decimal?)0m,
                    maxPainMagnetStrength = (decimal?)0.8m,
                    maxPainCurve = Array.Empty<object>(),
                    atmCallImpliedVolatility = (decimal?)0.18m,
                    atmPutImpliedVolatility = (decimal?)0.19m,
                    atmPutCallSkew = (decimal?)0.01m,
                    rr25Skew = (decimal?)null,
                    acceptedContractCount = 2,
                    acceptedStrikeCount = 1,
                    componentCoverage = 0.8m,
                    warnings = Array.Empty<string>(),
                },
            },
            ivTermStructure = Array.Empty<object>(),
            nearToNextIvSlope = (decimal?)null,
            nearToFarIvSlope = (decimal?)null,
            ivTermStructureState = "INSUFFICIENT",
            inputSnapshotCount = 1,
            acceptedContractCount = 2,
            acceptedStrikeCount = 1,
            componentCoverage = 0.8m,
            dataQualityStatus = "VALID",
            isStale = false,
            isEligibleForFusion = true,
            revision = 0,
            evidence = Array.Empty<object>(),
            warnings = Array.Empty<string>(),
            selectionAuthority = false,
            executionAuthority = authorityDrift,
        };
    }

    return JsonSerializer.Serialize(
        new
        {
            outcome,
            output,
            reason = outcome == "IGNORED_INELIGIBLE" ? "Snapshot was ineligible" : null,
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
};

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class StaticSnapshotSource(OptionChainSnapshotDispatchV1? snapshot)
    : IOptionChainSnapshotDispatchSource
{
    public Task<OptionChainSnapshotDispatchV1?> LoadAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(snapshot);
}

internal sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://localhost:8000"),
        Timeout = TimeSpan.FromSeconds(30),
    };
}

internal sealed class CapturingHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    public string? LastInternalKey { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        LastInternalKey = request.Headers.TryGetValues(
            "X-ThesisPulse-Internal-Key",
            out var values)
            ? values.SingleOrDefault()
            : null;
        return responder(request);
    }
}
