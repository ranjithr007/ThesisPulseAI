using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Contracts.Risk.V1;

public static class SignalRiskEvaluationContractV1
{
    public const string ContractVersion = "1.0.0";
    public const string CommandType = "risk.signal-evaluation.requested.v1";
    public const string Eligible = "ELIGIBLE";
    public const string Rejected = "REJECTED";

    public const string RiskEvaluating = "RISK_EVALUATING";
    public const string RiskApproved = "RISK_APPROVED";
    public const string RiskRejected = "RISK_REJECTED";
    public const string RiskRestricted = "RISK_RESTRICTED";
    public const string RiskRetryPending = "RISK_RETRY_PENDING";
    public const string RiskExpired = "RISK_EXPIRED";
}

public sealed record SignalRiskEvaluationIntakeV1(
    Guid MessageUid,
    string CorrelationId,
    Guid? CausationMessageUid,
    SignalGeneratedV1 Signal,
    FusionSignalLineageV1 Lineage,
    PortfolioRiskSnapshotV1 Portfolio,
    OperationalRiskStateV1 Operations,
    string RiskPolicyVersion,
    DateTimeOffset AsOfUtc);

public sealed record SignalRiskEvaluationCommandV1(
    Guid CommandUid,
    Guid RequestUid,
    Guid SourceMessageUid,
    string CorrelationId,
    Guid? CausationMessageUid,
    Guid SignalUid,
    Guid ThesisUid,
    RiskDecisionRequestV1 Request,
    FusionSignalLineageV1 Lineage,
    string ContractVersion,
    DateTimeOffset CreatedAtUtc);

public sealed record SignalRiskEvaluationProjectionResultV1(
    string Outcome,
    SignalRiskEvaluationCommandV1? Command,
    IReadOnlyCollection<string> Reasons);
