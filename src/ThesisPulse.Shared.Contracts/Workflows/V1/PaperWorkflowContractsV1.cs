using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Shared.Contracts.Workflows.V1;

public static class PaperWorkflowContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string PaperEnvironment = "PAPER";

    public const string Running = "RUNNING";
    public const string RetryPending = "RETRY_PENDING";
    public const string Completed = "COMPLETED";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";

    public const string StepPending = "PENDING";
    public const string StepRunning = "RUNNING";
    public const string StepSucceeded = "SUCCEEDED";
    public const string StepRejected = "REJECTED";
    public const string StepFailed = "FAILED";
}

public static class PaperWorkflowStepCodeV1
{
    public const string Thesis = "THESIS";
    public const string RiskDecision = "RISK_DECISION";
    public const string TradePlan = "TRADE_PLAN";
    public const string ExecutionCommand = "EXECUTION_COMMAND";
    public const string OrderSubmit = "ORDER_SUBMIT";
    public const string OrderAcknowledge = "ORDER_ACKNOWLEDGE";
    public const string OrderFillPrefix = "ORDER_FILL_";
    public const string PortfolioProjectionPrefix = "PORTFOLIO_PROJECTION_";
    public const string Reconciliation = "RECONCILIATION";
}

public sealed record TradePlanTemplateV1(
    string PositionIntent,
    TradeEntryProposalV1 Entry,
    decimal StopLossPrice,
    string StopOrderType,
    decimal? StopLimitPrice,
    IReadOnlyCollection<TradeTargetProposalV1> Targets,
    decimal LotSize,
    decimal? RequestedQuantity,
    decimal? MinimumExecutionQuantity,
    bool AllowPartialFill,
    decimal MaximumSlippageFraction,
    string TimeInForce,
    TradeSessionV1 Session,
    ExitPolicyV1 ExitPolicy,
    string ExecutionPolicyVersion);

public sealed record PaperFillSliceV1(
    int Sequence,
    decimal QuantityFraction,
    decimal? FillPrice);

public sealed record PaperFillSimulationV1(
    IReadOnlyCollection<PaperFillSliceV1> Fills,
    string PortfolioCode,
    string ReconciliationTriggerType);

public sealed record PaperWorkflowStartRequestV1(
    Guid RequestUid,
    string IdempotencyKey,
    string CorrelationId,
    Guid SourceMessageUid,
    ThesisFusionRequestV1 ThesisRequest,
    PortfolioRiskSnapshotV1 Portfolio,
    OperationalRiskStateV1 RiskOperations,
    string RiskPolicyVersion,
    TradePlanTemplateV1 TradePlan,
    ExecutionOperationalStateV1 ExecutionOperations,
    PaperFillSimulationV1 FillSimulation,
    DateTimeOffset AsOfUtc);

public sealed record PaperWorkflowStepSnapshotV1(
    Guid StepUid,
    string StepCode,
    int Sequence,
    string Status,
    int AttemptCount,
    string? OutputReference,
    bool Retryable,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record PaperWorkflowSnapshotV1(
    Guid WorkflowUid,
    Guid RequestUid,
    string IdempotencyKey,
    string CorrelationId,
    Guid SourceMessageUid,
    string Environment,
    string InstrumentKey,
    string PrimaryTimeframe,
    string Status,
    string? CurrentStep,
    int AttemptCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    IReadOnlyCollection<PaperWorkflowStepSnapshotV1> Steps);

public sealed record PaperWorkflowResultV1(
    PaperWorkflowSnapshotV1 Workflow,
    ThesisFusionResultV1? Thesis,
    RiskDecisionV1? RiskDecision,
    TradePlanBuildResultV1? TradePlan,
    ExecutionCommandResultV1? Execution,
    PaperOrderSnapshotV1? PaperOrder,
    PortfolioLedgerSnapshotV1? Portfolio,
    LedgerReconciliationResultV1? Reconciliation);
