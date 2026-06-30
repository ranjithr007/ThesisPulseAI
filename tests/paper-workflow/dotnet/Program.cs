using Microsoft.Extensions.Logging.Abstractions;
using ThesisPulse.Operations.Service;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Contracts.Workflows.V1;

var failures = new List<string>();

await RunAsync("complete evidence-to-ledger PAPER workflow", async () =>
{
    var gateway = new FakeGateway();
    var coordinator = CreateCoordinator(gateway, out _);
    var result = await coordinator.RunAsync(CreateRequest());

    AssertEqual(PaperWorkflowContractV1.Completed, result.Workflow.Status);
    AssertEqual(PaperOrderStateContractV1.Filled, result.PaperOrder?.State);
    AssertEqual(PortfolioLedgerContractV1.Reconciled, result.Reconciliation?.Status);
    AssertEqual(11, result.Workflow.Steps.Count);
    AssertEqual(2, gateway.ProjectFillCalls);
    AssertEqual(1, gateway.ReconcileCalls);
});

await RunAsync("completed workflow is idempotently replayed", async () =>
{
    var gateway = new FakeGateway();
    var coordinator = CreateCoordinator(gateway, out _);
    var request = CreateRequest();
    var first = await coordinator.RunAsync(request);
    var counters = gateway.CounterSnapshot();
    var second = await coordinator.RunAsync(request);

    AssertEqual(first.Workflow.WorkflowUid, second.Workflow.WorkflowUid);
    AssertEqual(PaperWorkflowContractV1.Completed, second.Workflow.Status);
    AssertEqual(counters, gateway.CounterSnapshot());
});

await RunAsync("risk rejection stops the workflow", async () =>
{
    var gateway = new FakeGateway { RejectRisk = true };
    var coordinator = CreateCoordinator(gateway, out _);
    var result = await coordinator.RunAsync(CreateRequest());

    AssertEqual(PaperWorkflowContractV1.Rejected, result.Workflow.Status);
    AssertEqual(1, gateway.RiskCalls);
    AssertEqual(0, gateway.TradePlanCalls);
    AssertTrue(
        result.Workflow.Steps.Any(step =>
            step.StepCode == PaperWorkflowStepCodeV1.RiskDecision &&
            step.Status == PaperWorkflowContractV1.StepRejected),
        "Risk step must be persisted as rejected.");
});

await RunAsync("retry resumes after completed steps", async () =>
{
    var gateway = new FakeGateway { TradePlanFailuresRemaining = 1 };
    var coordinator = CreateCoordinator(gateway, out _);
    var first = await coordinator.RunAsync(CreateRequest());

    AssertEqual(PaperWorkflowContractV1.RetryPending, first.Workflow.Status);
    var second = await coordinator.ResumeAsync(first.Workflow.WorkflowUid);

    AssertEqual(PaperWorkflowContractV1.Completed, second.Workflow.Status);
    AssertEqual(1, gateway.ThesisCalls);
    AssertEqual(1, gateway.RiskCalls);
    AssertEqual(2, gateway.TradePlanCalls);
    AssertEqual(2, second.Workflow.AttemptCount);
});

await RunAsync("unknown running step fails closed", async () =>
{
    var gateway = new FakeGateway();
    var coordinator = CreateCoordinator(gateway, out var store);
    var request = CreateRequest();
    var workflowUid = DeterministicGuidV1.Create(request.RequestUid, "paper-workflow.v1");
    var step = new StoredPaperWorkflowStep(
        new PaperWorkflowStepSnapshotV1(
            DeterministicGuidV1.Create(workflowUid, "step.THESIS.v1"),
            PaperWorkflowStepCodeV1.Thesis,
            1,
            PaperWorkflowContractV1.StepRunning,
            1,
            null,
            false,
            request.AsOfUtc,
            null,
            null,
            null),
        "{}",
        null);
    var snapshot = new PaperWorkflowSnapshotV1(
        workflowUid,
        request.RequestUid,
        request.IdempotencyKey,
        request.CorrelationId,
        request.SourceMessageUid,
        PaperWorkflowContractV1.PaperEnvironment,
        request.ThesisRequest.InstrumentKey,
        request.ThesisRequest.PrimaryTimeframe,
        PaperWorkflowContractV1.Running,
        PaperWorkflowStepCodeV1.Thesis,
        1,
        request.AsOfUtc,
        request.AsOfUtc,
        null,
        null,
        null,
        null,
        [step.Snapshot]);
    await store.CreateAsync(new StoredPaperWorkflow(
        snapshot,
        request,
        null,
        new Dictionary<string, StoredPaperWorkflowStep>(StringComparer.Ordinal)
        {
            [step.Snapshot.StepCode] = step,
        }));

    var result = await coordinator.ResumeAsync(workflowUid);

    AssertEqual(PaperWorkflowContractV1.Failed, result.Workflow.Status);
    AssertEqual("UNKNOWN_STEP_OUTCOME", result.Workflow.LastErrorCode);
    AssertEqual(0, gateway.ThesisCalls);
});

await RunAsync("invalid fill fractions are rejected before persistence", async () =>
{
    var gateway = new FakeGateway();
    var coordinator = CreateCoordinator(gateway, out _);
    var request = CreateRequest() with
    {
        FillSimulation = new PaperFillSimulationV1(
            [
                new PaperFillSliceV1(1, 0.4m, null),
                new PaperFillSliceV1(2, 0.5m, null),
            ],
            "PRIMARY-PAPER",
            "OPERATOR_REQUEST"),
    };

    try
    {
        await coordinator.RunAsync(request);
        throw new InvalidOperationException("Expected validation failure.");
    }
    catch (PaperWorkflowValidationException exception)
    {
        AssertTrue(
            exception.Reasons.Contains("FILL_FRACTIONS_MUST_TOTAL_ONE", StringComparer.Ordinal),
            "Expected fill-fraction validation reason.");
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} PAPER workflow test(s) failed.");
    return 1;
}

Console.WriteLine("All PAPER workflow orchestration tests passed.");
return 0;

PaperWorkflowCoordinator CreateCoordinator(
    FakeGateway gateway,
    out InMemoryPaperWorkflowStore store)
{
    store = new InMemoryPaperWorkflowStore();
    return new PaperWorkflowCoordinator(
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
}

static PaperWorkflowStartRequestV1 CreateRequest()
{
    var now = new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero);
    var correlationId = Guid.NewGuid().ToString("D");
    return new PaperWorkflowStartRequestV1(
        Guid.NewGuid(),
        $"paper-workflow-{Guid.NewGuid():N}",
        correlationId,
        Guid.NewGuid(),
        new ThesisFusionRequestV1(
            Guid.NewGuid(),
            correlationId,
            "NSE|RELIANCE",
            "5m",
            now,
            "fusion-weights-v1.0.0",
            [
                new DirectionalEvidenceV1(
                    "DIRECTIONAL",
                    "1.0.0",
                    "5m",
                    EvidenceDirectionV1.Long,
                    80m,
                    85m,
                    now,
                    ["TEST"]),
            ],
            new RegimeEvidenceV1(
                "TRENDING_UP",
                "1.0.0",
                EvidenceDirectionV1.Long,
                80m,
                now,
                ["TEST"]),
            [
                new TimeframeConfirmationV1(
                    "5m",
                    EvidenceDirectionV1.Long,
                    80m,
                    85m,
                    true,
                    now,
                    ["TEST"]),
            ]),
        new PortfolioRiskSnapshotV1(
            "PRIMARY-PAPER",
            "PAPER",
            1_000_000m,
            500_000m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0,
            Array.Empty<PortfolioPositionV1>(),
            now),
        new OperationalRiskStateV1(
            false,
            false,
            true,
            true,
            true,
            true,
            now),
        "risk-policy-v1.0.0",
        new TradePlanTemplateV1(
            "INTRADAY",
            new TradeEntryProposalV1(
                "MARKET",
                100m,
                null,
                null,
                99.5m,
                100.5m),
            95m,
            "STOP_MARKET",
            null,
            [new TradeTargetProposalV1(1, 110m, 1m)],
            1m,
            100m,
            1m,
            true,
            0.001m,
            "DAY",
            new TradeSessionV1(
                DateOnly.FromDateTime(now.UtcDateTime),
                now.AddMinutes(-1),
                now.AddMinutes(5),
                now.AddHours(5)),
            new ExitPolicyV1(true, true, true, true, "exit-policy-v1.0.0"),
            "execution-policy-v1.0.0"),
        new ExecutionOperationalStateV1(
            false,
            false,
            true,
            true,
            true,
            now),
        new PaperFillSimulationV1(
            [
                new PaperFillSliceV1(1, 0.4m, 100m),
                new PaperFillSliceV1(2, 0.6m, 101m),
            ],
            "PRIMARY-PAPER",
            "OPERATOR_REQUEST"),
        now);
}

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
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }
}

sealed class FakeGateway : IPaperWorkflowGateway
{
    private readonly Dictionary<Guid, PaperOrderSnapshotV1> _orders = new();

    public bool RejectRisk { get; init; }

    public int TradePlanFailuresRemaining { get; set; }

    public int ThesisCalls { get; private set; }

    public int RiskCalls { get; private set; }

    public int TradePlanCalls { get; private set; }

    public int ExecutionCalls { get; private set; }

    public int OrderEventCalls { get; private set; }

    public int ProjectFillCalls { get; private set; }

    public int ReconcileCalls { get; private set; }

    public string CounterSnapshot() =>
        $"{ThesisCalls}:{RiskCalls}:{TradePlanCalls}:{ExecutionCalls}:" +
        $"{OrderEventCalls}:{ProjectFillCalls}:{ReconcileCalls}";

    public Task<ThesisFusionResultV1> EvaluateThesisAsync(
        ThesisFusionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ThesisCalls++;
        var thesisUid = DeterministicGuidV1.Create(request.RequestUid, "thesis.v1");
        var candidate = new CanonicalCandidateSignalV1(
            DeterministicGuidV1.Create(request.RequestUid, "candidate-signal.v1"),
            ThesisFusionContractV1.CandidateStatus,
            request.InstrumentKey,
            EvidenceDirectionV1.Long,
            request.PrimaryTimeframe,
            80m,
            85m,
            request.AsOfUtc,
            request.WeightConfigurationVersion,
            thesisUid);
        return Task.FromResult(new ThesisFusionResultV1(
            thesisUid,
            request.RequestUid,
            correlationId,
            request.InstrumentKey,
            ThesisFusionContractV1.CandidateStatus,
            EvidenceDirectionV1.Long,
            80m,
            10m,
            85m,
            "TEST",
            Array.Empty<string>(),
            Array.Empty<ThesisEvidenceV1>(),
            candidate,
            "1.0.0",
            request.WeightConfigurationVersion,
            request.AsOfUtc));
    }

    public Task<RiskDecisionV1> EvaluateRiskAsync(
        RiskDecisionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        RiskCalls++;
        var approved = !RejectRisk;
        return Task.FromResult(new RiskDecisionV1(
            DeterministicGuidV1.Create(request.RequestUid, "risk-decision.v1"),
            request.RequestUid,
            correlationId,
            request.Candidate.SignalUid,
            request.Candidate.ThesisUid,
            request.Candidate.InstrumentKey,
            "PAPER",
            request.Candidate.Direction,
            approved ? RiskDecisionContractV1.Approved : RiskDecisionContractV1.Rejected,
            approved ? Array.Empty<string>() : ["TEST_RISK_REJECTION"],
            Array.Empty<RiskCheckV1>(),
            approved
                ? new RiskBudgetV1(1_000m, 100_000m, 100m, request.AsOfUtc.AddMinutes(1))
                : null,
            request.RiskPolicyVersion,
            "1.0.0",
            request.AsOfUtc));
    }

    public Task<TradePlanBuildResultV1> BuildTradePlanAsync(
        TradePlanBuildRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        TradePlanCalls++;
        if (TradePlanFailuresRemaining > 0)
        {
            TradePlanFailuresRemaining--;
            throw new PaperWorkflowGatewayException("Transient trade-plan failure.", true);
        }

        var quantity = request.RequestedQuantity ?? 100m;
        var plan = new TradePlanV1(
            DeterministicGuidV1.Create(request.RequestUid, "trade-plan.v1"),
            request.RiskDecision.RiskDecisionUid,
            request.RiskDecision.ThesisUid,
            request.RiskDecision.SignalUid,
            correlationId,
            "PAPER",
            request.RiskDecision.InstrumentKey,
            request.RiskDecision.Direction,
            request.RiskDecision.Direction == EvidenceDirectionV1.Long ? "BUY" : "SELL",
            request.PositionIntent,
            new TradePlanEntryV1(
                request.Entry.OrderType,
                request.Entry.ReferencePrice,
                request.Entry.LimitPrice,
                request.Entry.TriggerPrice,
                request.Entry.MinimumAcceptablePrice,
                request.Entry.MaximumAcceptablePrice),
            quantity,
            request.MinimumExecutionQuantity,
            request.AllowPartialFill,
            new TradePlanStopLossV1(
                request.StopLossPrice,
                request.StopOrderType,
                request.StopLimitPrice,
                true),
            request.Targets.Select(target => new TradePlanTargetV1(
                target.Sequence,
                target.Price,
                target.QuantityFraction)).ToArray(),
            request.MaximumSlippageFraction,
            request.TimeInForce,
            request.Session,
            request.ExitPolicy,
            500m,
            quantity * request.Entry.ReferencePrice,
            2m,
            request.ExecutionPolicyVersion,
            TradePlanContractV1.Ready,
            false,
            request.AsOfUtc,
            request.AsOfUtc.AddSeconds(30));
        return Task.FromResult(new TradePlanBuildResultV1(
            request.RequestUid,
            correlationId,
            TradePlanContractV1.Ready,
            Array.Empty<string>(),
            Array.Empty<TradePlanCheckV1>(),
            plan,
            "1.0.0",
            request.ExecutionPolicyVersion,
            request.AsOfUtc));
    }

    public Task<ExecutionCommandResultV1> AuthorizeExecutionAsync(
        ExecutionCommandRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ExecutionCalls++;
        var commandUid = DeterministicGuidV1.Create(request.RequestUid, "execution-command.v1");
        var orderUid = DeterministicGuidV1.Create(request.RequestUid, "paper-order.v1");
        var plan = request.TradePlan;
        var command = new ExecutionCommandV1(
            commandUid,
            request.RequestUid,
            plan.TradePlanUid,
            plan.RiskDecisionUid,
            plan.ThesisUid,
            plan.SignalUid,
            correlationId,
            request.IdempotencyKey,
            "PAPER",
            plan.InstrumentKey,
            plan.Direction,
            plan.Side,
            plan.PositionIntent,
            plan.Entry,
            plan.ApprovedQuantity,
            plan.MinimumExecutionQuantity,
            plan.AllowPartialFill,
            plan.StopLoss,
            plan.Targets,
            plan.MaximumSlippageFraction,
            plan.TimeInForce,
            plan.Session,
            plan.ExitPolicy,
            request.ExecutionPolicyVersion,
            ExecutionCommandContractV1.Authorized,
            true,
            false,
            false,
            request.AsOfUtc,
            request.AsOfUtc.AddSeconds(30));
        var order = new PaperOrderSnapshotV1(
            orderUid,
            commandUid,
            plan.TradePlanUid,
            plan.RiskDecisionUid,
            plan.ThesisUid,
            plan.SignalUid,
            correlationId,
            request.IdempotencyKey,
            "PAPER",
            plan.InstrumentKey,
            plan.Direction,
            plan.Side,
            PaperOrderStateContractV1.Created,
            plan.ApprovedQuantity,
            0m,
            plan.ApprovedQuantity,
            null,
            plan.AllowPartialFill,
            1,
            request.AsOfUtc,
            request.AsOfUtc,
            null,
            null);
        _orders[orderUid] = order;
        return Task.FromResult(new ExecutionCommandResultV1(
            request.RequestUid,
            request.IdempotencyKey,
            ExecutionCommandContractV1.Authorized,
            Array.Empty<string>(),
            Array.Empty<ExecutionGateCheckV1>(),
            command,
            order,
            "1.0.0",
            request.ExecutionPolicyVersion,
            request.AsOfUtc));
    }

    public Task<PaperOrderTransitionResultV1> ApplyOrderEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        OrderEventCalls++;
        var order = _orders[paperOrderUid];
        Guid? fillUid = null;
        order = request.EventType switch
        {
            PaperOrderEventContractV1.Submit => order with
            {
                State = PaperOrderStateContractV1.Submitted,
                Version = order.Version + 1,
                UpdatedAtUtc = request.OccurredAtUtc,
            },
            PaperOrderEventContractV1.Acknowledge => order with
            {
                State = PaperOrderStateContractV1.Acknowledged,
                Version = order.Version + 1,
                UpdatedAtUtc = request.OccurredAtUtc,
            },
            PaperOrderEventContractV1.Fill => ApplyFill(order, request, out fillUid),
            _ => throw new InvalidOperationException("Unsupported fake event."),
        };
        _orders[paperOrderUid] = order;
        return Task.FromResult(new PaperOrderTransitionResultV1(
            true,
            false,
            Array.Empty<string>(),
            order,
            request.OccurredAtUtc,
            fillUid));
    }

    public Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ProjectFillCalls++;
        var position = new PositionLedgerSnapshotV1(
            Guid.NewGuid(),
            request.PortfolioCode,
            "PAPER",
            "NSE|RELIANCE",
            "INTRADAY",
            EvidenceDirectionV1.Long,
            100m,
            100.6m,
            10_060m,
            10_060m,
            0m,
            0m,
            0m,
            0m,
            "OPEN",
            ProjectFillCalls,
            request.AsOfUtc,
            request.AsOfUtc,
            null,
            request.AsOfUtc);
        var portfolio = new PortfolioLedgerSnapshotV1(
            Guid.NewGuid(),
            request.PortfolioCode,
            "PAPER",
            "FIFO",
            "INR",
            [position],
            Array.Empty<CashLedgerSnapshotV1>(),
            10_060m,
            10_060m,
            0m,
            0m,
            1,
            request.AsOfUtc);
        return Task.FromResult(new PortfolioFillProjectionResultV1(
            request.RequestUid,
            request.FillUid,
            PortfolioLedgerContractV1.Projected,
            Array.Empty<string>(),
            position,
            portfolio,
            request.AsOfUtc));
    }

    public Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ReconcileCalls++;
        return Task.FromResult(new LedgerReconciliationResultV1(
            request.RequestUid,
            DeterministicGuidV1.Create(request.RequestUid, "reconciliation-run.v1"),
            request.PortfolioCode,
            PortfolioLedgerContractV1.Reconciled,
            3,
            Array.Empty<LedgerDiscrepancyV1>(),
            false,
            true,
            request.AsOfUtc,
            request.AsOfUtc));
    }

    private static PaperOrderSnapshotV1 ApplyFill(
        PaperOrderSnapshotV1 order,
        PaperOrderEventRequestV1 request,
        out Guid? fillUid)
    {
        var quantity = request.FillQuantity ?? 0m;
        var price = request.FillPrice ?? 0m;
        var filled = order.FilledQuantity + quantity;
        var remaining = order.RequestedQuantity - filled;
        var priorNotional = (order.AverageFillPrice ?? 0m) * order.FilledQuantity;
        var average = (priorNotional + quantity * price) / filled;
        fillUid = request.EventUid;
        return order with
        {
            State = remaining == 0m
                ? PaperOrderStateContractV1.Filled
                : PaperOrderStateContractV1.PartiallyFilled,
            FilledQuantity = filled,
            RemainingQuantity = remaining,
            AverageFillPrice = average,
            Version = order.Version + 1,
            UpdatedAtUtc = request.OccurredAtUtc,
            TerminalAtUtc = remaining == 0m ? request.OccurredAtUtc : null,
        };
    }
}
