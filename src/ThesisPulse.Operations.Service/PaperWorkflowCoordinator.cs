using System.Collections.Concurrent;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Contracts.Workflows.V1;

namespace ThesisPulse.Operations.Service;

public sealed record PaperWorkflowOptions
{
    public bool Enabled { get; init; }

    public int MaximumAttempts { get; init; } = 3;

    public int RetryDelaySeconds { get; init; } = 15;

    public int RecoveryIntervalSeconds { get; init; } = 15;

    public int RecoveryBatchSize { get; init; } = 25;
}

public sealed class PaperWorkflowValidationException(
    IReadOnlyCollection<string> reasons) : Exception(string.Join(", ", reasons))
{
    public IReadOnlyCollection<string> Reasons { get; } = reasons;
}

public sealed class PaperWorkflowIdempotencyException(string message) : Exception(message);

public sealed class PaperWorkflowCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IPaperWorkflowStore _store;
    private readonly IPaperWorkflowGateway _gateway;
    private readonly PaperWorkflowOptions _options;
    private readonly ILogger<PaperWorkflowCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _workflowLocks = new();

    public PaperWorkflowCoordinator(
        IPaperWorkflowStore store,
        IPaperWorkflowGateway gateway,
        PaperWorkflowOptions options,
        ILogger<PaperWorkflowCoordinator> logger)
    {
        _store = store;
        _gateway = gateway;
        _options = options;
        _logger = logger;
    }

    public async Task<PaperWorkflowResultV1> RunAsync(
        PaperWorkflowStartRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var existing = await _store.FindByIdempotencyAsync(
            request.IdempotencyKey.Trim(),
            cancellationToken);
        if (existing is not null)
        {
            EnsureSameRequest(existing, request);
            return await ResumeAsync(existing.Snapshot.WorkflowUid, cancellationToken);
        }

        var workflowUid = DeterministicGuidV1.Create(
            request.RequestUid,
            "paper-workflow.v1");
        var now = request.AsOfUtc;
        var snapshot = new PaperWorkflowSnapshotV1(
            workflowUid,
            request.RequestUid,
            request.IdempotencyKey.Trim(),
            request.CorrelationId.Trim(),
            request.SourceMessageUid,
            PaperWorkflowContractV1.PaperEnvironment,
            request.ThesisRequest.InstrumentKey,
            request.ThesisRequest.PrimaryTimeframe,
            PaperWorkflowContractV1.Running,
            null,
            0,
            now,
            now,
            null,
            null,
            null,
            null,
            Array.Empty<PaperWorkflowStepSnapshotV1>());
        var created = await _store.CreateAsync(
            new StoredPaperWorkflow(
                snapshot,
                request with
                {
                    IdempotencyKey = request.IdempotencyKey.Trim(),
                    CorrelationId = request.CorrelationId.Trim(),
                },
                null,
                new Dictionary<string, StoredPaperWorkflowStep>(StringComparer.Ordinal)),
            cancellationToken);
        EnsureSameRequest(created, request);
        return await ResumeAsync(created.Snapshot.WorkflowUid, cancellationToken);
    }

    public async Task<PaperWorkflowResultV1> ResumeAsync(
        Guid workflowUid,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _workflowLocks.GetOrAdd(workflowUid, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var workflow = await _store.GetAsync(workflowUid, cancellationToken)
                ?? throw new KeyNotFoundException($"Paper workflow '{workflowUid}' was not found.");
            if (IsTerminal(workflow.Snapshot.Status))
            {
                return ReadResult(workflow);
            }

            var unknown = workflow.Steps.Values.FirstOrDefault(step =>
                step.Snapshot.Status == PaperWorkflowContractV1.StepRunning);
            if (unknown is not null)
            {
                workflow = await FinishFailedAsync(
                    workflow,
                    "UNKNOWN_STEP_OUTCOME",
                    $"Step '{unknown.Snapshot.StepCode}' was RUNNING when recovery began. " +
                    "The workflow failed closed and requires reconciliation.",
                    cancellationToken);
                return ReadResult(workflow);
            }

            if (workflow.Snapshot.AttemptCount >= _options.MaximumAttempts)
            {
                workflow = await FinishFailedAsync(
                    workflow,
                    "MAXIMUM_ATTEMPTS_EXCEEDED",
                    "The workflow exhausted its configured recovery attempts.",
                    cancellationToken);
                return ReadResult(workflow);
            }

            workflow = workflow with
            {
                Snapshot = workflow.Snapshot with
                {
                    Status = PaperWorkflowContractV1.Running,
                    CurrentStep = null,
                    AttemptCount = workflow.Snapshot.AttemptCount + 1,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    NextAttemptAtUtc = null,
                    LastErrorCode = null,
                    LastErrorMessage = null,
                },
            };
            workflow = await _store.SaveAsync(Normalize(workflow), cancellationToken);

            try
            {
                return await ExecuteAsync(workflow, cancellationToken);
            }
            catch (WorkflowStepException exception)
            {
                workflow = exception.Workflow;
                if (exception.Rejected)
                {
                    workflow = await FinishRejectedAsync(
                        workflow,
                        exception.Code,
                        exception.Message,
                        cancellationToken);
                }
                else if (exception.Retryable &&
                    workflow.Snapshot.AttemptCount < _options.MaximumAttempts)
                {
                    workflow = workflow with
                    {
                        Snapshot = workflow.Snapshot with
                        {
                            Status = PaperWorkflowContractV1.RetryPending,
                            CurrentStep = exception.StepCode,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                            NextAttemptAtUtc = DateTimeOffset.UtcNow.AddSeconds(
                                _options.RetryDelaySeconds),
                            CompletedAtUtc = null,
                            LastErrorCode = exception.Code,
                            LastErrorMessage = exception.Message,
                        },
                    };
                    workflow = await _store.SaveAsync(
                        Normalize(workflow),
                        cancellationToken);
                }
                else
                {
                    workflow = await FinishFailedAsync(
                        workflow,
                        exception.Code,
                        exception.Message,
                        cancellationToken);
                }

                return ReadResult(workflow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "PAPER workflow failed unexpectedly. WorkflowUid={WorkflowUid}",
                    workflowUid);
                workflow = await FinishFailedAsync(
                    workflow,
                    "UNHANDLED_WORKFLOW_FAILURE",
                    exception.Message,
                    cancellationToken);
                return ReadResult(workflow);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<PaperWorkflowResultV1?> GetAsync(
        Guid workflowUid,
        CancellationToken cancellationToken = default)
    {
        var workflow = await _store.GetAsync(workflowUid, cancellationToken);
        return workflow is null ? null : ReadResult(workflow);
    }

    private async Task<PaperWorkflowResultV1> ExecuteAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken)
    {
        var request = workflow.Request;
        var workflowUid = workflow.Snapshot.WorkflowUid;
        var thesisRequest = request.ThesisRequest with
        {
            RequestUid = DeterministicGuidV1.Create(workflowUid, "request.thesis.v1"),
            CorrelationId = request.CorrelationId,
            AsOfUtc = request.AsOfUtc,
        };
        var thesisStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.Thesis,
            1,
            thesisRequest,
            token => _gateway.EvaluateThesisAsync(
                thesisRequest,
                request.CorrelationId,
                token),
            result => result.Candidate is not null,
            result => result.ThesisUid.ToString("D"),
            result => result.GateFailures,
            cancellationToken);
        workflow = thesisStep.Workflow;
        var thesis = thesisStep.Response;
        var candidate = thesis.Candidate
            ?? throw new InvalidOperationException("Accepted thesis step has no candidate.");

        var riskRequest = new RiskDecisionRequestV1(
            DeterministicGuidV1.Create(workflowUid, "request.risk.v1"),
            request.CorrelationId,
            candidate,
            request.Portfolio,
            request.RiskOperations,
            request.RiskPolicyVersion,
            request.AsOfUtc);
        var riskStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.RiskDecision,
            2,
            riskRequest,
            token => _gateway.EvaluateRiskAsync(
                riskRequest,
                request.CorrelationId,
                token),
            result => result.Decision == RiskDecisionContractV1.Approved,
            result => result.RiskDecisionUid.ToString("D"),
            result => result.Reasons,
            cancellationToken);
        workflow = riskStep.Workflow;
        var riskDecision = riskStep.Response;

        var template = request.TradePlan;
        var tradePlanRequest = new TradePlanBuildRequestV1(
            DeterministicGuidV1.Create(workflowUid, "request.trade-plan.v1"),
            request.CorrelationId,
            riskDecision,
            template.PositionIntent,
            template.Entry,
            template.StopLossPrice,
            template.StopOrderType,
            template.StopLimitPrice,
            template.Targets,
            template.LotSize,
            template.RequestedQuantity,
            template.MinimumExecutionQuantity,
            template.AllowPartialFill,
            template.MaximumSlippageFraction,
            template.TimeInForce,
            template.Session,
            template.ExitPolicy,
            template.ExecutionPolicyVersion,
            request.AsOfUtc);
        var planStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.TradePlan,
            3,
            tradePlanRequest,
            token => _gateway.BuildTradePlanAsync(
                tradePlanRequest,
                request.CorrelationId,
                token),
            result => result.Status == TradePlanContractV1.Ready && result.TradePlan is not null,
            result => result.TradePlan?.TradePlanUid.ToString("D"),
            result => result.Reasons,
            cancellationToken);
        workflow = planStep.Workflow;
        var planResult = planStep.Response;
        var tradePlan = planResult.TradePlan
            ?? throw new InvalidOperationException("Accepted trade-plan step has no plan.");

        var executionRequest = new ExecutionCommandRequestV1(
            DeterministicGuidV1.Create(workflowUid, "request.execution.v1"),
            $"{request.IdempotencyKey}:execution:v1",
            request.CorrelationId,
            tradePlan,
            request.ExecutionOperations,
            template.ExecutionPolicyVersion,
            request.AsOfUtc);
        var executionStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.ExecutionCommand,
            4,
            executionRequest,
            token => _gateway.AuthorizeExecutionAsync(
                executionRequest,
                request.CorrelationId,
                token),
            result => result.Status == ExecutionCommandContractV1.Authorized &&
                result.Command is not null && result.PaperOrder is not null,
            result => result.Command?.ExecutionCommandUid.ToString("D"),
            result => result.Reasons,
            cancellationToken);
        workflow = executionStep.Workflow;
        var execution = executionStep.Response;
        var order = execution.PaperOrder
            ?? throw new InvalidOperationException("Authorized execution has no paper order.");

        var submitRequest = EventRequest(
            workflowUid,
            PaperWorkflowStepCodeV1.OrderSubmit,
            PaperOrderEventContractV1.Submit,
            request.AsOfUtc.AddMilliseconds(1));
        var submitStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.OrderSubmit,
            5,
            submitRequest,
            token => _gateway.ApplyOrderEventAsync(
                order.PaperOrderUid,
                submitRequest,
                request.CorrelationId,
                token),
            result => result.Applied && result.PaperOrder is not null,
            result => result.PaperOrder?.PaperOrderUid.ToString("D"),
            result => result.Reasons,
            cancellationToken);
        workflow = submitStep.Workflow;
        order = submitStep.Response.PaperOrder!;

        var acknowledgeRequest = EventRequest(
            workflowUid,
            PaperWorkflowStepCodeV1.OrderAcknowledge,
            PaperOrderEventContractV1.Acknowledge,
            request.AsOfUtc.AddMilliseconds(2));
        var acknowledgeStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.OrderAcknowledge,
            6,
            acknowledgeRequest,
            token => _gateway.ApplyOrderEventAsync(
                order.PaperOrderUid,
                acknowledgeRequest,
                request.CorrelationId,
                token),
            result => result.Applied && result.PaperOrder is not null,
            result => result.PaperOrder?.PaperOrderUid.ToString("D"),
            result => result.Reasons,
            cancellationToken);
        workflow = acknowledgeStep.Workflow;
        order = acknowledgeStep.Response.PaperOrder!;

        PortfolioLedgerSnapshotV1? portfolio = null;
        var orderedFills = request.FillSimulation.Fills
            .OrderBy(fill => fill.Sequence)
            .ToArray();
        var remaining = tradePlan.ApprovedQuantity;
        var nextStepSequence = 7;
        for (var index = 0; index < orderedFills.Length; index++)
        {
            var fill = orderedFills[index];
            var quantity = index == orderedFills.Length - 1
                ? remaining
                : Math.Round(tradePlan.ApprovedQuantity * fill.QuantityFraction, 6);
            remaining -= quantity;
            var fillCode = $"{PaperWorkflowStepCodeV1.OrderFillPrefix}{fill.Sequence:D2}";
            var fillRequest = new PaperOrderEventRequestV1(
                DeterministicGuidV1.Create(workflowUid, $"event.{fillCode}.v1"),
                PaperOrderEventContractV1.Fill,
                quantity,
                fill.FillPrice ?? tradePlan.Entry.ReferencePrice,
                "Deterministic PAPER fill simulation.",
                request.AsOfUtc.AddMilliseconds(nextStepSequence));
            var fillStep = await RunStepAsync(
                workflow,
                fillCode,
                nextStepSequence++,
                fillRequest,
                token => _gateway.ApplyOrderEventAsync(
                    order.PaperOrderUid,
                    fillRequest,
                    request.CorrelationId,
                    token),
                result => result.Applied && result.PaperOrder is not null && result.FillUid is not null,
                result => result.FillUid?.ToString("D"),
                result => result.Reasons,
                cancellationToken);
            workflow = fillStep.Workflow;
            order = fillStep.Response.PaperOrder!;
            var fillUid = fillStep.Response.FillUid
                ?? throw new InvalidOperationException("Applied fill has no fill UID.");

            var projectionCode = $"{PaperWorkflowStepCodeV1.PortfolioProjectionPrefix}{fill.Sequence:D2}";
            var projectionRequest = new PortfolioFillProjectionRequestV1(
                DeterministicGuidV1.Create(workflowUid, $"request.{projectionCode}.v1"),
                fillUid,
                request.FillSimulation.PortfolioCode,
                request.CorrelationId,
                request.AsOfUtc.AddMilliseconds(nextStepSequence));
            var projectionStep = await RunStepAsync(
                workflow,
                projectionCode,
                nextStepSequence++,
                projectionRequest,
                token => _gateway.ProjectFillAsync(
                    projectionRequest,
                    request.CorrelationId,
                    token),
                result => result.Status is PortfolioLedgerContractV1.Projected or
                    PortfolioLedgerContractV1.Duplicate,
                result => result.Position?.PositionUid.ToString("D"),
                result => result.Reasons,
                cancellationToken);
            workflow = projectionStep.Workflow;
            portfolio = projectionStep.Response.Portfolio;
        }

        var reconciliationRequest = new LedgerReconciliationRequestV1(
            DeterministicGuidV1.Create(workflowUid, "request.reconciliation.v1"),
            request.FillSimulation.PortfolioCode,
            request.CorrelationId,
            request.FillSimulation.ReconciliationTriggerType,
            request.AsOfUtc.AddMilliseconds(nextStepSequence));
        var reconciliationStep = await RunStepAsync(
            workflow,
            PaperWorkflowStepCodeV1.Reconciliation,
            nextStepSequence,
            reconciliationRequest,
            token => _gateway.ReconcileAsync(
                reconciliationRequest,
                request.CorrelationId,
                token),
            _ => true,
            result => result.ReconciliationRunUid.ToString("D"),
            _ => Array.Empty<string>(),
            cancellationToken);
        workflow = reconciliationStep.Workflow;
        var reconciliation = reconciliationStep.Response;
        if (reconciliation.Status != PortfolioLedgerContractV1.Reconciled ||
            reconciliation.BlocksNewExposure)
        {
            workflow = await FinishFailedAsync(
                workflow,
                "RECONCILIATION_DISCREPANCY",
                "The workflow completed its PAPER fills, but reconciliation found a material discrepancy.",
                cancellationToken);
            return ReadResult(workflow);
        }

        workflow = workflow with
        {
            Snapshot = workflow.Snapshot with
            {
                Status = PaperWorkflowContractV1.Completed,
                CurrentStep = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastErrorCode = null,
                LastErrorMessage = null,
            },
        };
        var result = BuildResult(workflow);
        workflow = workflow with { ResultJson = JsonSerializer.Serialize(result, JsonOptions) };
        workflow = await _store.SaveAsync(Normalize(workflow), cancellationToken);
        return BuildResult(workflow);
    }

    private async Task<StepRun<TResponse>> RunStepAsync<TRequest, TResponse>(
        StoredPaperWorkflow workflow,
        string stepCode,
        int sequence,
        TRequest request,
        Func<CancellationToken, Task<TResponse>> action,
        Func<TResponse, bool> accepted,
        Func<TResponse, string?> outputReference,
        Func<TResponse, IReadOnlyCollection<string>> rejectionReasons,
        CancellationToken cancellationToken)
    {
        if (workflow.Steps.TryGetValue(stepCode, out var existing))
        {
            if (existing.Snapshot.Status == PaperWorkflowContractV1.StepSucceeded)
            {
                return new StepRun<TResponse>(
                    workflow,
                    Deserialize<TResponse>(existing.ResponseJson, stepCode));
            }

            if (existing.Snapshot.Status == PaperWorkflowContractV1.StepRejected)
            {
                throw new WorkflowStepException(
                    workflow,
                    stepCode,
                    existing.Snapshot.ErrorCode ?? "STEP_REJECTED",
                    existing.Snapshot.ErrorMessage ?? $"Step '{stepCode}' was rejected.",
                    false,
                    true);
            }

            if (existing.Snapshot.Status == PaperWorkflowContractV1.StepFailed &&
                !existing.Snapshot.Retryable)
            {
                throw new WorkflowStepException(
                    workflow,
                    stepCode,
                    existing.Snapshot.ErrorCode ?? "STEP_FAILED",
                    existing.Snapshot.ErrorMessage ?? $"Step '{stepCode}' failed.",
                    false,
                    false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var started = new StoredPaperWorkflowStep(
            new PaperWorkflowStepSnapshotV1(
                DeterministicGuidV1.Create(workflow.Snapshot.WorkflowUid, $"step.{stepCode}.v1"),
                stepCode,
                sequence,
                PaperWorkflowContractV1.StepRunning,
                (existing?.Snapshot.AttemptCount ?? 0) + 1,
                null,
                false,
                now,
                null,
                null,
                null),
            JsonSerializer.Serialize(request, JsonOptions),
            null);
        workflow = WithStep(workflow, started) with
        {
            Snapshot = workflow.Snapshot with
            {
                CurrentStep = stepCode,
                UpdatedAtUtc = now,
            },
        };
        workflow = await _store.SaveAsync(Normalize(workflow), cancellationToken);

        try
        {
            var response = await action(cancellationToken);
            var responseJson = JsonSerializer.Serialize(response, JsonOptions);
            if (!accepted(response))
            {
                var reasons = rejectionReasons(response)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var message = reasons.Length == 0
                    ? $"Step '{stepCode}' was rejected by its owning service."
                    : string.Join(", ", reasons);
                var rejected = started with
                {
                    Snapshot = started.Snapshot with
                    {
                        Status = PaperWorkflowContractV1.StepRejected,
                        CompletedAtUtc = DateTimeOffset.UtcNow,
                        ErrorCode = "DOWNSTREAM_REJECTED",
                        ErrorMessage = message,
                    },
                    ResponseJson = responseJson,
                };
                workflow = await _store.SaveAsync(
                    Normalize(WithStep(workflow, rejected)),
                    cancellationToken);
                throw new WorkflowStepException(
                    workflow,
                    stepCode,
                    "DOWNSTREAM_REJECTED",
                    message,
                    false,
                    true);
            }

            var succeeded = started with
            {
                Snapshot = started.Snapshot with
                {
                    Status = PaperWorkflowContractV1.StepSucceeded,
                    OutputReference = outputReference(response),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ErrorCode = null,
                    ErrorMessage = null,
                },
                ResponseJson = responseJson,
            };
            workflow = WithStep(workflow, succeeded) with
            {
                Snapshot = workflow.Snapshot with
                {
                    CurrentStep = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            workflow = await _store.SaveAsync(Normalize(workflow), cancellationToken);
            return new StepRun<TResponse>(workflow, response);
        }
        catch (WorkflowStepException)
        {
            throw;
        }
        catch (PaperWorkflowGatewayException exception)
        {
            workflow = await SaveFailedStepAsync(
                workflow,
                started,
                exception.Retryable,
                "DOWNSTREAM_TRANSPORT_FAILURE",
                exception.Message,
                cancellationToken);
            throw new WorkflowStepException(
                workflow,
                stepCode,
                "DOWNSTREAM_TRANSPORT_FAILURE",
                exception.Message,
                exception.Retryable,
                false,
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            workflow = await SaveFailedStepAsync(
                workflow,
                started,
                false,
                "STEP_EXECUTION_FAILURE",
                exception.Message,
                cancellationToken);
            throw new WorkflowStepException(
                workflow,
                stepCode,
                "STEP_EXECUTION_FAILURE",
                exception.Message,
                false,
                false,
                exception);
        }
    }

    private async Task<StoredPaperWorkflow> SaveFailedStepAsync(
        StoredPaperWorkflow workflow,
        StoredPaperWorkflowStep started,
        bool retryable,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var failed = started with
        {
            Snapshot = started.Snapshot with
            {
                Status = PaperWorkflowContractV1.StepFailed,
                Retryable = retryable,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ErrorCode = code,
                ErrorMessage = message,
            },
        };
        return await _store.SaveAsync(
            Normalize(WithStep(workflow, failed)),
            cancellationToken);
    }

    private async Task<StoredPaperWorkflow> FinishRejectedAsync(
        StoredPaperWorkflow workflow,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        workflow = workflow with
        {
            Snapshot = workflow.Snapshot with
            {
                Status = PaperWorkflowContractV1.Rejected,
                CurrentStep = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastErrorCode = code,
                LastErrorMessage = message,
            },
        };
        var result = BuildResult(workflow);
        workflow = workflow with { ResultJson = JsonSerializer.Serialize(result, JsonOptions) };
        return await _store.SaveAsync(Normalize(workflow), cancellationToken);
    }

    private async Task<StoredPaperWorkflow> FinishFailedAsync(
        StoredPaperWorkflow workflow,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        workflow = workflow with
        {
            Snapshot = workflow.Snapshot with
            {
                Status = PaperWorkflowContractV1.Failed,
                CurrentStep = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastErrorCode = code,
                LastErrorMessage = message,
            },
        };
        var result = BuildResult(workflow);
        workflow = workflow with { ResultJson = JsonSerializer.Serialize(result, JsonOptions) };
        return await _store.SaveAsync(Normalize(workflow), cancellationToken);
    }

    private static PaperOrderEventRequestV1 EventRequest(
        Guid workflowUid,
        string stepCode,
        string eventType,
        DateTimeOffset occurredAtUtc) =>
        new(
            DeterministicGuidV1.Create(workflowUid, $"event.{stepCode}.v1"),
            eventType,
            null,
            null,
            null,
            occurredAtUtc);

    private static StoredPaperWorkflow WithStep(
        StoredPaperWorkflow workflow,
        StoredPaperWorkflowStep step)
    {
        var steps = workflow.Steps.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        steps[step.Snapshot.StepCode] = step;
        return workflow with { Steps = steps };
    }

    private static StoredPaperWorkflow Normalize(StoredPaperWorkflow workflow) =>
        workflow with
        {
            Snapshot = workflow.Snapshot with
            {
                Steps = workflow.Steps.Values
                    .OrderBy(step => step.Snapshot.Sequence)
                    .Select(step => step.Snapshot)
                    .ToArray(),
            },
        };

    private static PaperWorkflowResultV1 ReadResult(StoredPaperWorkflow workflow)
    {
        if (!string.IsNullOrWhiteSpace(workflow.ResultJson))
        {
            var stored = JsonSerializer.Deserialize<PaperWorkflowResultV1>(
                workflow.ResultJson,
                JsonOptions);
            if (stored is not null)
            {
                return stored with { Workflow = workflow.Snapshot };
            }
        }

        return BuildResult(workflow);
    }

    private static PaperWorkflowResultV1 BuildResult(StoredPaperWorkflow workflow)
    {
        var orderSteps = workflow.Steps.Values
            .Where(step =>
                step.Snapshot.Status == PaperWorkflowContractV1.StepSucceeded &&
                (step.Snapshot.StepCode == PaperWorkflowStepCodeV1.OrderAcknowledge ||
                 step.Snapshot.StepCode.StartsWith(
                     PaperWorkflowStepCodeV1.OrderFillPrefix,
                     StringComparison.Ordinal)))
            .OrderBy(step => step.Snapshot.Sequence)
            .ToArray();
        var projectionSteps = workflow.Steps.Values
            .Where(step =>
                step.Snapshot.Status == PaperWorkflowContractV1.StepSucceeded &&
                step.Snapshot.StepCode.StartsWith(
                    PaperWorkflowStepCodeV1.PortfolioProjectionPrefix,
                    StringComparison.Ordinal))
            .OrderBy(step => step.Snapshot.Sequence)
            .ToArray();

        return new PaperWorkflowResultV1(
            workflow.Snapshot,
            Response<ThesisFusionResultV1>(workflow, PaperWorkflowStepCodeV1.Thesis),
            Response<RiskDecisionV1>(workflow, PaperWorkflowStepCodeV1.RiskDecision),
            Response<TradePlanBuildResultV1>(workflow, PaperWorkflowStepCodeV1.TradePlan),
            Response<ExecutionCommandResultV1>(workflow, PaperWorkflowStepCodeV1.ExecutionCommand),
            orderSteps.Length == 0
                ? null
                : Deserialize<PaperOrderTransitionResultV1>(
                    orderSteps[^1].ResponseJson,
                    orderSteps[^1].Snapshot.StepCode).PaperOrder,
            projectionSteps.Length == 0
                ? null
                : Deserialize<PortfolioFillProjectionResultV1>(
                    projectionSteps[^1].ResponseJson,
                    projectionSteps[^1].Snapshot.StepCode).Portfolio,
            Response<LedgerReconciliationResultV1>(
                workflow,
                PaperWorkflowStepCodeV1.Reconciliation));
    }

    private static T? Response<T>(StoredPaperWorkflow workflow, string stepCode)
    {
        return workflow.Steps.TryGetValue(stepCode, out var step) &&
            !string.IsNullOrWhiteSpace(step.ResponseJson)
                ? JsonSerializer.Deserialize<T>(step.ResponseJson, JsonOptions)
                : default;
    }

    private static T Deserialize<T>(string? json, string stepCode) =>
        string.IsNullOrWhiteSpace(json)
            ? throw new InvalidOperationException(
                $"Succeeded step '{stepCode}' has no stored response.")
            : JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Stored response for step '{stepCode}' could not be deserialized.");

    private static bool IsTerminal(string status) =>
        status is PaperWorkflowContractV1.Completed or
            PaperWorkflowContractV1.Rejected or
            PaperWorkflowContractV1.Failed;

    private static void EnsureSameRequest(
        StoredPaperWorkflow existing,
        PaperWorkflowStartRequestV1 request)
    {
        if (existing.Snapshot.RequestUid != request.RequestUid ||
            existing.Snapshot.SourceMessageUid != request.SourceMessageUid ||
            !string.Equals(
                existing.Snapshot.CorrelationId,
                request.CorrelationId.Trim(),
                StringComparison.Ordinal))
        {
            throw new PaperWorkflowIdempotencyException(
                "The workflow idempotency key is already bound to another request lineage.");
        }
    }

    private static void Validate(PaperWorkflowStartRequestV1 request)
    {
        var failures = new List<string>();
        if (request.RequestUid == Guid.Empty) failures.Add("REQUEST_UID_REQUIRED");
        if (request.SourceMessageUid == Guid.Empty) failures.Add("SOURCE_MESSAGE_UID_REQUIRED");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) failures.Add("IDEMPOTENCY_KEY_REQUIRED");
        if (!Guid.TryParse(request.CorrelationId, out _)) failures.Add("CORRELATION_ID_INVALID");
        if (request.ThesisRequest is null) failures.Add("THESIS_REQUEST_REQUIRED");
        else
        {
            if (!string.Equals(
                    request.ThesisRequest.CorrelationId,
                    request.CorrelationId,
                    StringComparison.Ordinal))
                failures.Add("THESIS_CORRELATION_MISMATCH");
            if (string.IsNullOrWhiteSpace(request.ThesisRequest.InstrumentKey))
                failures.Add("INSTRUMENT_KEY_REQUIRED");
            if (string.IsNullOrWhiteSpace(request.ThesisRequest.PrimaryTimeframe))
                failures.Add("PRIMARY_TIMEFRAME_REQUIRED");
        }

        if (!string.Equals(
                request.Portfolio.Environment,
                PaperWorkflowContractV1.PaperEnvironment,
                StringComparison.OrdinalIgnoreCase))
            failures.Add("PAPER_PORTFOLIO_REQUIRED");
        if (request.FillSimulation is null || request.FillSimulation.Fills.Count == 0)
            failures.Add("FILL_SIMULATION_REQUIRED");
        else
        {
            var fills = request.FillSimulation.Fills;
            if (fills.Count > 10) failures.Add("TOO_MANY_FILL_SLICES");
            if (fills.Any(fill =>
                    fill.Sequence < 1 ||
                    fill.QuantityFraction <= 0 ||
                    fill.FillPrice is <= 0))
                failures.Add("INVALID_FILL_SLICE");
            if (fills.Select(fill => fill.Sequence).Distinct().Count() != fills.Count)
                failures.Add("DUPLICATE_FILL_SEQUENCE");
            if (fills.Sum(fill => fill.QuantityFraction) != 1m)
                failures.Add("FILL_FRACTIONS_MUST_TOTAL_ONE");
            if (fills.Count > 1 && !request.TradePlan.AllowPartialFill)
                failures.Add("MULTIPLE_FILLS_REQUIRE_PARTIAL_FILL_POLICY");
            if (string.IsNullOrWhiteSpace(request.FillSimulation.PortfolioCode))
                failures.Add("PORTFOLIO_CODE_REQUIRED");
            if (string.IsNullOrWhiteSpace(request.FillSimulation.ReconciliationTriggerType))
                failures.Add("RECONCILIATION_TRIGGER_REQUIRED");
        }

        if (failures.Count > 0)
        {
            throw new PaperWorkflowValidationException(
                failures.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    private sealed record StepRun<TResponse>(
        StoredPaperWorkflow Workflow,
        TResponse Response);

    private sealed class WorkflowStepException : Exception
    {
        public WorkflowStepException(
            StoredPaperWorkflow workflow,
            string stepCode,
            string code,
            string message,
            bool retryable,
            bool rejected,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Workflow = workflow;
            StepCode = stepCode;
            Code = code;
            Retryable = retryable;
            Rejected = rejected;
        }

        public StoredPaperWorkflow Workflow { get; }
        public string StepCode { get; }
        public string Code { get; }
        public bool Retryable { get; }
        public bool Rejected { get; }
    }
}
