namespace ThesisPulse.Shared.Contracts.Execution.V1;

public sealed record PaperTradeLifecycleLineageEvidenceV1(
    string Stage,
    Guid EntityUid,
    Guid CorrelationUid,
    Guid? CausationUid,
    DateTimeOffset OccurredAtUtc,
    string SourceTable);

public sealed record PaperTradeLifecycleOperationalStateV1(
    string ScopeType,
    string ScopeId,
    string EffectiveOperatingMode,
    Guid? SourceControlUid,
    bool AllowsNewExposure,
    bool AllowsRiskReducingExits,
    bool RequiresOperatorReview,
    DateTimeOffset EvaluatedAtUtc,
    string EvaluationVersion);

public sealed record PaperTradeLifecycleAcceptanceCheckV1(
    string Code,
    string Outcome,
    string Message,
    IReadOnlyCollection<string> EvidenceReferences);

public sealed record PaperTradeLifecycleAcceptanceReportV1(
    string ContractVersion,
    string Environment,
    Guid CorrelationUid,
    string? PortfolioCode,
    string Outcome,
    DateTimeOffset EvaluatedAtUtc,
    PaperTradeLifecycleSummaryV1 Summary,
    IReadOnlyCollection<PaperTradeLifecycleLineageEvidenceV1> Lineage,
    IReadOnlyCollection<PaperTradeLifecycleOperationalStateV1> OperationalStates,
    IReadOnlyCollection<PaperTradeLifecycleAcceptanceCheckV1> Checks);
