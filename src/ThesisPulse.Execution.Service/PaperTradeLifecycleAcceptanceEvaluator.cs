using ThesisPulse.Shared.Contracts.Execution.V1;

namespace ThesisPulse.Execution.Service;

public sealed class PaperTradeLifecycleAcceptanceEvaluator
{
    private static readonly string[] MandatoryStages =
    [
        "SIGNAL",
        "THESIS",
        "RISK",
        "TRADE_PLAN",
        "EXECUTION_COMMAND",
        "ORDER_EVENT",
        "FILL",
        "POSITION_EVENT",
        "PNL",
    ];

    private static readonly HashSet<string> CausationRequiredStages = new(
        ["THESIS", "RISK", "TRADE_PLAN", "EXECUTION_COMMAND", "ORDER_EVENT", "FILL", "POSITION_EVENT"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RestrictiveModes = new(
        ["RESTRICTED", "CLOSE_ONLY", "PAUSED", "HALTED", "RECOVERY"],
        StringComparer.OrdinalIgnoreCase);

    public PaperTradeLifecycleAcceptanceReportV1 Evaluate(
        string environment,
        PaperTradeLifecycleDetailV1 lifecycle,
        IReadOnlyCollection<PaperTradeLifecycleLineageEvidenceV1> lineage,
        IReadOnlyCollection<PaperTradeLifecycleOperationalStateV1> operationalStates,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(operationalStates);

        var checks = new List<PaperTradeLifecycleAcceptanceCheckV1>();
        var summary = lifecycle.Summary;
        var evidence = lineage.ToArray();
        var states = operationalStates.ToArray();

        AddCheck(
            checks,
            "PAPER_ENVIRONMENT",
            string.Equals(environment, PaperTradeLifecycleContractV1.PaperEnvironment,
                StringComparison.OrdinalIgnoreCase)
                ? PaperTradeLifecycleAcceptanceContractV1.Passed
                : PaperTradeLifecycleAcceptanceContractV1.Failed,
            string.Equals(environment, PaperTradeLifecycleContractV1.PaperEnvironment,
                StringComparison.OrdinalIgnoreCase)
                ? "Lifecycle evidence is restricted to PAPER."
                : $"Unexpected lifecycle environment '{environment}'.",
            [environment]);

        var complete = summary.IsComplete &&
            string.Equals(summary.LifecycleStatus, PaperTradeLifecycleContractV1.Complete,
                StringComparison.OrdinalIgnoreCase) &&
            summary.PnlSnapshotUid.HasValue;
        AddCheck(
            checks,
            "LIFECYCLE_COMPLETE",
            complete
                ? PaperTradeLifecycleAcceptanceContractV1.Passed
                : PaperTradeLifecycleAcceptanceContractV1.Incomplete,
            complete
                ? "The lifecycle is complete through an authoritative P&L snapshot."
                : "The lifecycle has not completed through authoritative P&L.",
            EntityReferences(summary));

        AddCheck(
            checks,
            "FRESHNESS",
            summary.IsStale
                ? PaperTradeLifecycleAcceptanceContractV1.Failed
                : PaperTradeLifecycleAcceptanceContractV1.Passed,
            summary.IsStale
                ? "The lifecycle is stale at evaluation time."
                : "The lifecycle is within the backend freshness policy.",
            [$"observed:{summary.ObservedAtUtc:O}", $"last-activity:{summary.LastActivityAtUtc:O}"]);

        var executionWarning = summary.Warnings.FirstOrDefault(item =>
            item.StartsWith("EXECUTION_ERROR:", StringComparison.OrdinalIgnoreCase));
        var warningOutcome = executionWarning is not null
            ? PaperTradeLifecycleAcceptanceContractV1.Failed
            : summary.Warnings.Count > 0
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : PaperTradeLifecycleAcceptanceContractV1.Passed;
        AddCheck(
            checks,
            "NO_UNRESOLVED_WARNINGS",
            warningOutcome,
            warningOutcome == PaperTradeLifecycleAcceptanceContractV1.Passed
                ? "No unresolved lifecycle warnings are present."
                : $"Lifecycle warnings remain: {string.Join(", ", summary.Warnings)}",
            summary.Warnings);

        var evidenceByStage = evidence
            .GroupBy(item => item.Stage, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var missingStages = MandatoryStages
            .Where(stage => !evidenceByStage.ContainsKey(stage))
            .ToArray();
        AddCheck(
            checks,
            "MANDATORY_STAGE_COVERAGE",
            missingStages.Length == 0
                ? PaperTradeLifecycleAcceptanceContractV1.Passed
                : PaperTradeLifecycleAcceptanceContractV1.Incomplete,
            missingStages.Length == 0
                ? "All mandatory lifecycle stages have authoritative lineage evidence."
                : $"Missing authoritative lineage for: {string.Join(", ", missingStages)}.",
            evidence.Select(DescribeEvidence).ToArray());

        var expectedOrder = MandatoryStages
            .Select((stage, index) => new { stage, index })
            .ToDictionary(item => item.stage, item => item.index, StringComparer.OrdinalIgnoreCase);
        var observedOrder = evidence
            .Where(item => expectedOrder.ContainsKey(item.Stage))
            .Select(item => expectedOrder[item.Stage])
            .ToArray();
        var ordered = observedOrder.Zip(observedOrder.Skip(1), (left, right) => left < right).All(value => value);
        AddCheck(
            checks,
            "MANDATORY_STAGE_ORDER",
            missingStages.Length > 0
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : ordered
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            missingStages.Length > 0
                ? "Stage order cannot be proven until all mandatory stages are present."
                : ordered
                    ? "Mandatory lifecycle stages are ordered from Signal through P&L."
                    : "Mandatory lifecycle evidence is not in canonical order.",
            evidence.Select(item => item.Stage).ToArray());

        var correlationMismatches = evidence
            .Where(item => item.CorrelationUid != summary.CorrelationUid)
            .ToArray();
        AddCheck(
            checks,
            "CORRELATION_CONTINUITY",
            evidence.Length == 0
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : correlationMismatches.Length == 0
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            evidence.Length == 0
                ? "No lineage evidence is available to prove correlation continuity."
                : correlationMismatches.Length == 0
                    ? "Every available stage preserves the lifecycle correlation UID."
                    : "One or more stages contain a different correlation UID.",
            evidence.Select(item => $"{item.Stage}:{item.CorrelationUid}").ToArray());

        var requiredCausationEvidence = evidence
            .Where(item => CausationRequiredStages.Contains(item.Stage))
            .ToArray();
        var missingCausation = requiredCausationEvidence
            .Where(item => !item.CausationUid.HasValue)
            .ToArray();
        var selfCausation = requiredCausationEvidence
            .Where(item => item.CausationUid == item.EntityUid ||
                           item.CausationUid == item.CorrelationUid)
            .ToArray();
        var causationOutcome = missingStages.Any(stage => CausationRequiredStages.Contains(stage)) ||
                               missingCausation.Length > 0
            ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
            : selfCausation.Length > 0
                ? PaperTradeLifecycleAcceptanceContractV1.Failed
                : PaperTradeLifecycleAcceptanceContractV1.Passed;
        AddCheck(
            checks,
            "CAUSATION_EVIDENCE",
            causationOutcome,
            causationOutcome == PaperTradeLifecycleAcceptanceContractV1.Passed
                ? "Every causation-required stage has a non-self causation UID."
                : causationOutcome == PaperTradeLifecycleAcceptanceContractV1.Failed
                    ? "A causation UID is self-referential or reuses the correlation UID."
                    : "One or more causation-required stages are missing causation evidence.",
            requiredCausationEvidence.Select(item =>
                $"{item.Stage}:{item.CausationUid?.ToString() ?? "missing"}").ToArray());

        var quantityEvidenceComplete = summary.RequestedQuantity.HasValue &&
                                       summary.FilledQuantity.HasValue &&
                                       summary.AverageFillPrice.HasValue;
        var quantitiesValid = quantityEvidenceComplete &&
                              summary.RequestedQuantity > 0 &&
                              summary.FilledQuantity > 0 &&
                              summary.FilledQuantity <= summary.RequestedQuantity &&
                              summary.AverageFillPrice > 0;
        AddCheck(
            checks,
            "EXECUTION_QUANTITY_CONSISTENCY",
            !quantityEvidenceComplete
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : quantitiesValid
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            !quantityEvidenceComplete
                ? "Requested quantity, filled quantity, or average fill price is unavailable."
                : quantitiesValid
                    ? "Execution quantities and average fill price are internally consistent."
                    : "Execution quantities or average fill price are invalid.",
            [$"requested:{summary.RequestedQuantity}", $"filled:{summary.FilledQuantity}",
                $"average-fill:{summary.AverageFillPrice}"]);

        var portfolioEvidenceComplete = summary.PositionUid.HasValue &&
                                        summary.PnlSnapshotUid.HasValue &&
                                        summary.PositionQuantity.HasValue &&
                                        summary.NetPnlAmount.HasValue;
        var portfolioEvidenceValid = portfolioEvidenceComplete && summary.PositionQuantity >= 0;
        AddCheck(
            checks,
            "POSITION_AND_PNL_EVIDENCE",
            !portfolioEvidenceComplete
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : portfolioEvidenceValid
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            !portfolioEvidenceComplete
                ? "Position or P&L evidence is incomplete."
                : portfolioEvidenceValid
                    ? "Position and P&L evidence is present and internally valid."
                    : "Position evidence contains an invalid quantity.",
            [$"position:{summary.PositionUid}", $"position-quantity:{summary.PositionQuantity}",
                $"pnl:{summary.PnlSnapshotUid}", $"net-pnl:{summary.NetPnlAmount}"]);

        AddCheck(
            checks,
            "OPERATIONAL_STATE_COVERAGE",
            states.Length > 0
                ? PaperTradeLifecycleAcceptanceContractV1.Passed
                : PaperTradeLifecycleAcceptanceContractV1.Incomplete,
            states.Length > 0
                ? "Applicable authoritative operating-state evidence is available."
                : "No applicable authoritative operating-state evidence is available.",
            states.Select(DescribeState).ToArray());

        var exitsBlocked = states.Where(item => !item.AllowsRiskReducingExits).ToArray();
        AddCheck(
            checks,
            "RISK_REDUCING_EXIT_SAFETY",
            states.Length == 0
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : exitsBlocked.Length == 0
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            states.Length == 0
                ? "Exit safety cannot be proven without operating-state evidence."
                : exitsBlocked.Length == 0
                    ? "All applicable operating states preserve risk-reducing exits."
                    : "At least one applicable operating state blocks risk-reducing exits.",
            states.Select(DescribeState).ToArray());

        var unsafeExposure = states.Where(item =>
            RestrictiveModes.Contains(item.EffectiveOperatingMode) && item.AllowsNewExposure).ToArray();
        AddCheck(
            checks,
            "RESTRICTIVE_MODE_EXPOSURE_SAFETY",
            states.Length == 0
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : unsafeExposure.Length == 0
                    ? PaperTradeLifecycleAcceptanceContractV1.Passed
                    : PaperTradeLifecycleAcceptanceContractV1.Failed,
            states.Length == 0
                ? "Exposure safety cannot be proven without operating-state evidence."
                : unsafeExposure.Length == 0
                    ? "No restrictive operating mode silently allows new exposure."
                    : "A restrictive operating mode incorrectly allows new exposure.",
            states.Select(DescribeState).ToArray());

        var outcome = checks.Any(item => item.Outcome == PaperTradeLifecycleAcceptanceContractV1.Failed)
            ? PaperTradeLifecycleAcceptanceContractV1.Failed
            : checks.Any(item => item.Outcome == PaperTradeLifecycleAcceptanceContractV1.Incomplete)
                ? PaperTradeLifecycleAcceptanceContractV1.Incomplete
                : PaperTradeLifecycleAcceptanceContractV1.Passed;

        return new PaperTradeLifecycleAcceptanceReportV1(
            PaperTradeLifecycleAcceptanceContractV1.ContractVersion,
            environment,
            summary.CorrelationUid,
            summary.PortfolioCode,
            outcome,
            evaluatedAtUtc,
            summary,
            evidence,
            states,
            checks);
    }

    private static void AddCheck(
        ICollection<PaperTradeLifecycleAcceptanceCheckV1> checks,
        string code,
        string outcome,
        string message,
        IReadOnlyCollection<string> evidenceReferences) =>
        checks.Add(new PaperTradeLifecycleAcceptanceCheckV1(
            code,
            outcome,
            message,
            evidenceReferences));

    private static IReadOnlyCollection<string> EntityReferences(PaperTradeLifecycleSummaryV1 summary) =>
    [
        $"signal:{summary.SignalUid}",
        $"thesis:{summary.ThesisUid}",
        $"risk:{summary.RiskDecisionUid}",
        $"trade-plan:{summary.TradePlanUid}",
        $"execution-command:{summary.ExecutionCommandUid}",
        $"order:{summary.OrderUid}",
        $"position:{summary.PositionUid}",
        $"pnl:{summary.PnlSnapshotUid}",
    ];

    private static string DescribeEvidence(PaperTradeLifecycleLineageEvidenceV1 evidence) =>
        $"{evidence.Stage}:{evidence.EntityUid}:{evidence.SourceTable}";

    private static string DescribeState(PaperTradeLifecycleOperationalStateV1 state) =>
        $"{state.ScopeType}:{state.ScopeId}:{state.EffectiveOperatingMode}";
}
