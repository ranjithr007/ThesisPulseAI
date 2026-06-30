using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Operations.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Contracts.Workflows.V1;

var failures = new List<string>();

await RunAsync("disabled intake does not load portfolio state", async () =>
{
    var context = new FakeContextProvider(CreatePortfolio(DateTimeOffset.UtcNow));
    var store = new InMemoryPaperWorkflowStore();
    var service = CreateService(
        EnabledOptions() with { Enabled = false },
        context,
        store,
        new RejectingGateway());

    var result = await service.IntakeAsync(CreateEvidence(DateTimeOffset.UtcNow));

    AssertEqual(FusionReadyEvidenceContractV1.Ignored, result.Status);
    AssertTrue(result.Reasons.Contains("AUTOMATIC_WORKFLOW_DISABLED"),
        "Disabled intake reason is required.");
    AssertEqual(0, context.CallCount);
});

await RunAsync("stale evidence fails before portfolio lookup", async () =>
{
    var context = new FakeContextProvider(CreatePortfolio(DateTimeOffset.UtcNow));
    var store = new InMemoryPaperWorkflowStore();
    var service = CreateService(
        EnabledOptions(),
        context,
        store,
        new RejectingGateway());

    var result = await service.IntakeAsync(
        CreateEvidence(DateTimeOffset.UtcNow.AddMinutes(-10)));

    AssertEqual(FusionReadyEvidenceContractV1.Rejected, result.Status);
    AssertTrue(result.Reasons.Contains("EVIDENCE_STALE"),
        "Stale evidence reason is required.");
    AssertEqual(0, context.CallCount);
});

await RunAsync("missing portfolio fails closed", async () =>
{
    var context = new FakeContextProvider(null);
    var store = new InMemoryPaperWorkflowStore();
    var service = CreateService(
        EnabledOptions(),
        context,
        store,
        new RejectingGateway());

    var result = await service.IntakeAsync(CreateEvidence(DateTimeOffset.UtcNow));

    AssertEqual(FusionReadyEvidenceContractV1.Rejected, result.Status);
    AssertTrue(result.Reasons.Contains("PORTFOLIO_NOT_FOUND"),
        "Missing portfolio reason is required.");
    AssertEqual(1, context.CallCount);
});

await RunAsync("healthy evidence maps into the canonical PAPER workflow", async () =>
{
    var now = DateTimeOffset.UtcNow;
    var profile = EnabledOptions(now);
    var context = new FakeContextProvider(CreatePortfolio(now));
    var store = new InMemoryPaperWorkflowStore();
    var gateway = new RejectingGateway();
    var service = CreateService(profile, context, store, gateway);
    var evidence = CreateEvidence(now);

    var result = await service.IntakeAsync(evidence);

    AssertEqual(FusionReadyEvidenceContractV1.Rejected, result.Status);
    AssertEqual(1, context.CallCount);
    AssertEqual(1, gateway.ThesisCallCount);
    AssertTrue(result.WorkflowUid is not null, "Workflow UID must be returned.");

    var stored = await store.GetAsync(result.WorkflowUid!.Value)
        ?? throw new InvalidOperationException("Stored workflow was not found.");
    var request = stored.Request;
    AssertEqual(evidence.SourceCandleMessageUid, request.SourceMessageUid);
    AssertEqual($"fusion-ready:{evidence.EvidenceUid:N}", request.IdempotencyKey);
    AssertEqual(evidence.InstrumentKey, request.ThesisRequest.InstrumentKey);
    AssertEqual(4, request.ThesisRequest.DirectionalEvidence.Count);
    AssertEqual(3, request.ThesisRequest.TimeframeConfirmations.Count);
    AssertEqual("PAPER", request.Portfolio.Environment);
    AssertEqual(1_000_000m, request.Portfolio.Equity);
    AssertEqual(1_000_000m, request.Portfolio.AvailableCash);
    AssertEqual(profile.LotSize, request.TradePlan.LotSize);
    AssertEqual(100m, request.TradePlan.Entry.ReferencePrice);
    AssertEqual(95m, request.TradePlan.StopLossPrice);
    AssertEqual(110m, request.TradePlan.Targets.Single().Price);
    AssertEqual(false, request.TradePlan.AllowPartialFill);
    AssertEqual(true, request.RiskOperations.MarketOpen);
    AssertEqual(true, request.ExecutionOperations.PaperGatewayHealthy);
    AssertEqual(1, request.FillSimulation.Fills.Count);
    AssertEqual(100m, request.FillSimulation.Fills.Single().FillPrice);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} automatic intake test(s) failed.");
    return 1;
}

Console.WriteLine("All automatic PAPER workflow intake tests passed.");
return 0;

static AutomaticPaperWorkflowIntakeService CreateService(
    AutomaticPaperWorkflowOptions options,
    FakeContextProvider context,
    InMemoryPaperWorkflowStore store,
    RejectingGateway gateway)
{
    var coordinator = new PaperWorkflowCoordinator(
        store,
        gateway,
        new PaperWorkflowOptions
        {
            Enabled = true,
            MaximumAttempts = 3,
            RetryDelaySeconds = 1,
            RecoveryIntervalSeconds = 5,
            RecoveryBatchSize = 25,
        },
        NullLogger<PaperWorkflowCoordinator>.Instance);
    return new AutomaticPaperWorkflowIntakeService(options, context, coordinator);
}

static AutomaticPaperWorkflowOptions EnabledOptions(DateTimeOffset? now = null)
{
    var instant = now ?? DateTimeOffset.UtcNow;
    var zone = ChooseTestTimeZone(instant);
    var local = TimeZoneInfo.ConvertTime(instant, zone);
    return new AutomaticPaperWorkflowOptions
    {
        Enabled = true,
        PortfolioCode = "PRIMARY-PAPER",
        AccountKey = "PRIMARY-PAPER",
        LotSize = 25m,
        RequestedQuantity = 100m,
        MinimumExecutionQuantity = 25m,
        AllowPartialFill = false,
        PositionIntent = "INTRADAY",
        TimeInForce = "DAY",
        RiskPolicyVersion = "risk-policy-v1.0.0",
        ExecutionPolicyVersion = "execution-policy-v1.0.0",
        ExitPolicyVersion = "exit-policy-v1.0.0",
        MarketTimeZone = zone.Id,
        MarketOpenLocal = TimeOnly.FromDateTime(local.DateTime.AddMinutes(-30)),
        NewEntryCutoffLocal = TimeOnly.FromDateTime(local.DateTime.AddMinutes(30)),
        MandatoryExitLocal = TimeOnly.FromDateTime(local.DateTime.AddMinutes(60)),
        MaximumEvidenceAgeSeconds = 120,
        CurrentDrawdownPercent = 0m,
        NewExposureEnabled = true,
        SessionCalendarHealthy = true,
        KillSwitchActive = false,
        TradingHalted = false,
        MarketDataHealthy = true,
        PortfolioStateHealthy = true,
        BrokerConnectivityHealthy = true,
        PaperGatewayHealthy = true,
        AllowTrailingStop = true,
        AllowBreakEvenMove = true,
        AllowTimeExit = true,
        AllowSignalExit = true,
    };
}

static TimeZoneInfo ChooseTestTimeZone(DateTimeOffset now)
{
    var candidates = new[]
    {
        "UTC",
        "Asia/Kolkata",
        "America/New_York",
        "Pacific/Honolulu",
    };
    foreach (var identifier in candidates)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(identifier);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            if (local.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
                local.TimeOfDay >= TimeSpan.FromHours(2) &&
                local.TimeOfDay <= TimeSpan.FromHours(21))
            {
                return zone;
            }
        }
        catch (TimeZoneNotFoundException)
        {
        }
    }

    throw new InvalidOperationException(
        "No stable weekday test timezone was available for the intake session test.");
}

static FusionReadyEvidenceV1 CreateEvidence(DateTimeOffset asOfUtc)
{
    var correlationId = Guid.NewGuid().ToString("D");
    var directional = new[]
    {
        Directional("TREND", "5m", asOfUtc, 1),
        Directional("MOMENTUM", "5m", asOfUtc, 2),
        Directional("TREND", "15m", asOfUtc, 3),
        Directional("MOMENTUM", "1h", asOfUtc, 4),
    };
    var confirmations = new[]
    {
        Confirmation("5m", asOfUtc, 10),
        Confirmation("15m", asOfUtc, 20),
        Confirmation("1h", asOfUtc, 30),
    };
    return new FusionReadyEvidenceV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        correlationId,
        "NSE_EQ|INE002A01018",
        "5m",
        asOfUtc,
        asOfUtc,
        "fusion-weights-v1.0.0",
        directional,
        new FusionRegimeEvidenceV1(
            Guid.NewGuid(),
            "TRENDING_UP_NORMAL",
            "1.0.0",
            "5m",
            "LONG",
            80m,
            asOfUtc,
            ["Regime supports long"]),
        confirmations,
        new FusionTradeProposalV1(
            "LONG",
            100m,
            99.5m,
            100.5m,
            95m,
            [new FusionTradeTargetProposalV1(1, 110m, 1m)],
            0.001m,
            "atr-trade-proposal-v1.0.0"),
        true,
        Array.Empty<string>());
}

static FusionDirectionalEvidenceV1 Directional(
    string engineCode,
    string timeframe,
    DateTimeOffset observedAtUtc,
    int seed) =>
    new(
        new Guid(seed, 0, 0, new byte[8]),
        engineCode,
        "1.0.0",
        timeframe,
        "LONG",
        80m,
        85m,
        observedAtUtc,
        [$"{engineCode} supports long"]);

static FusionTimeframeConfirmationV1 Confirmation(
    string timeframe,
    DateTimeOffset observedAtUtc,
    int seed) =>
    new(
        timeframe,
        new Guid(seed, 0, 0, new byte[8]),
        new Guid(seed + 1, 0, 0, new byte[8]),
        "LONG",
        80m,
        85m,
        true,
        observedAtUtc,
        [$"{timeframe} confirms long"]);

static PortfolioLedgerSnapshotV1 CreatePortfolio(DateTimeOffset asOfUtc) =>
    new(
        Guid.NewGuid(),
        "PRIMARY-PAPER",
        "PAPER",
        "FIFO",
        "INR",
        Array.Empty<PositionLedgerSnapshotV1>(),
        [
            new CashLedgerSnapshotV1(
                "PRIMARY-PAPER",
                "INR",
                1_000_000m,
                0m,
                0m,
                0m,
                1_000_000m,
                1_000_000m,
                1,
                1,
                asOfUtc),
        ],
        0m,
        0m,
        0m,
        0m,
        0,
        asOfUtc);

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Expected '{expected}' but received '{actual}'.");
    }
}

sealed class FakeContextProvider(PortfolioLedgerSnapshotV1? portfolio)
    : IAutomaticPaperWorkflowContextProvider
{
    public int CallCount { get; private set; }

    public Task<PortfolioLedgerSnapshotV1?> GetPortfolioAsync(
        string portfolioCode,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(portfolio);
    }
}

sealed class RejectingGateway : IPaperWorkflowGateway
{
    public int ThesisCallCount { get; private set; }

    public ThesisFusionRequestV1? CapturedThesisRequest { get; private set; }

    public Task<ThesisFusionResultV1> EvaluateThesisAsync(
        ThesisFusionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ThesisCallCount++;
        CapturedThesisRequest = request;
        return Task.FromResult(new ThesisFusionResultV1(
            Guid.NewGuid(),
            request.RequestUid,
            correlationId,
            request.InstrumentKey,
            "REJECTED_BY_FUSION",
            EvidenceDirectionV1.Neutral,
            50m,
            50m,
            0m,
            "Test rejection after intake mapping.",
            ["TEST_REJECTION"],
            Array.Empty<ThesisEvidenceV1>(),
            null,
            "test-fusion-v1",
            request.WeightConfigurationVersion,
            request.AsOfUtc));
    }

    public Task<RiskDecisionV1> EvaluateRiskAsync(
        RiskDecisionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Risk must not run after thesis rejection.");

    public Task<TradePlanBuildResultV1> BuildTradePlanAsync(
        TradePlanBuildRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Trade Plan must not run after thesis rejection.");

    public Task<ExecutionCommandResultV1> AuthorizeExecutionAsync(
        ExecutionCommandRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Execution must not run after thesis rejection.");

    public Task<PaperOrderTransitionResultV1> ApplyOrderEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Order events must not run after thesis rejection.");

    public Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Portfolio projection must not run after thesis rejection.");

    public Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Reconciliation must not run after thesis rejection.");
}
