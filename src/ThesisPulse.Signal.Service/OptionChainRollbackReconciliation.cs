namespace ThesisPulse.Signal.Service;

public sealed record OptionChainRollbackOptions
{
    public bool AutomaticRollbackEnabled { get; init; }
    public bool ExplicitRecoveryRequired { get; init; } = true;
    public int MaximumReconciliationDrift { get; init; }

    public void Validate()
    {
        if (MaximumReconciliationDrift < 0)
            throw new InvalidOperationException("Maximum reconciliation drift cannot be negative.");
    }
}

public sealed record OptionChainRolloutStateSnapshot(
    string Mode,
    bool WorkerExecutionSuppressed,
    string? LastReason,
    DateTimeOffset? ChangedAtUtc,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed class OptionChainRolloutState
{
    private readonly object _sync = new();
    private string _mode;
    private bool _workerExecutionSuppressed;
    private string? _lastReason;
    private DateTimeOffset? _changedAtUtc;

    public OptionChainRolloutState(OptionChainCanaryOptions canaryOptions)
    {
        _mode = canaryOptions.ParsedMode.ToString().ToUpperInvariant();
        _workerExecutionSuppressed = canaryOptions.ParsedMode is OptionChainRolloutMode.Disabled or OptionChainRolloutMode.RolledBack;
    }

    public OptionChainRolloutStateSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new(_mode, _workerExecutionSuppressed, _lastReason, _changedAtUtc, false, false);
        }
    }

    public OptionChainRolloutStateSnapshot Rollback(string reason, DateTimeOffset observedAtUtc)
    {
        lock (_sync)
        {
            _mode = "ROLLED_BACK";
            _workerExecutionSuppressed = true;
            _lastReason = string.IsNullOrWhiteSpace(reason) ? "UNSPECIFIED_GUARDRAIL_BREACH" : reason;
            _changedAtUtc = observedAtUtc;
            return Snapshot();
        }
    }

    public OptionChainRolloutStateSnapshot Recover(string targetMode, string reason, DateTimeOffset observedAtUtc)
    {
        var normalized = targetMode.Trim().ToUpperInvariant();
        if (normalized is not ("CANARY" or "EXPANDED"))
            throw new InvalidOperationException("Recovery target mode must be CANARY or EXPANDED.");

        lock (_sync)
        {
            _mode = normalized;
            _workerExecutionSuppressed = false;
            _lastReason = string.IsNullOrWhiteSpace(reason) ? "OPERATOR_RECOVERY" : reason;
            _changedAtUtc = observedAtUtc;
            return Snapshot();
        }
    }
}

public sealed record OptionChainReconciliationItem(
    Guid WorkUid,
    Guid SnapshotUid,
    Guid? OutputUid,
    Guid? OutputSourceMessageUid,
    IReadOnlyCollection<Guid> OutputSourceSnapshotUids,
    bool DuplicateOutcome,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed record OptionChainReconciliationReport(
    int Total,
    int Matched,
    int MissingOutput,
    int WorkLineageMismatch,
    int SnapshotLineageMismatch,
    int AuthorityViolation,
    int DuplicateWithoutOutput,
    int DriftCount,
    bool Healthy,
    IReadOnlyCollection<string> Reasons,
    bool SelectionAuthority,
    bool ExecutionAuthority,
    DateTimeOffset ObservedAtUtc);

public sealed class OptionChainEvidenceReconciler(OptionChainRollbackOptions options)
{
    public OptionChainReconciliationReport Reconcile(
        IReadOnlyCollection<OptionChainReconciliationItem> items,
        DateTimeOffset observedAtUtc)
    {
        options.Validate();
        var missing = 0;
        var workMismatch = 0;
        var snapshotMismatch = 0;
        var authorityViolation = 0;
        var duplicateWithoutOutput = 0;
        var matched = 0;

        foreach (var item in items)
        {
            var hasOutput = item.OutputUid.HasValue;
            if (!hasOutput)
            {
                if (item.DuplicateOutcome) duplicateWithoutOutput++;
                else missing++;
                continue;
            }

            var itemHealthy = true;
            if (item.OutputSourceMessageUid != item.WorkUid)
            {
                workMismatch++;
                itemHealthy = false;
            }
            if (!item.OutputSourceSnapshotUids.Contains(item.SnapshotUid))
            {
                snapshotMismatch++;
                itemHealthy = false;
            }
            if (item.SelectionAuthority || item.ExecutionAuthority)
            {
                authorityViolation++;
                itemHealthy = false;
            }
            if (itemHealthy) matched++;
        }

        var drift = missing + workMismatch + snapshotMismatch + authorityViolation + duplicateWithoutOutput;
        var reasons = new List<string>();
        if (missing > 0) reasons.Add("MISSING_OUTPUT");
        if (workMismatch > 0) reasons.Add("WORK_LINEAGE_MISMATCH");
        if (snapshotMismatch > 0) reasons.Add("SNAPSHOT_LINEAGE_MISMATCH");
        if (authorityViolation > 0) reasons.Add("AUTHORITY_VIOLATION");
        if (duplicateWithoutOutput > 0) reasons.Add("DUPLICATE_WITHOUT_CANONICAL_OUTPUT");
        if (drift > options.MaximumReconciliationDrift) reasons.Add("RECONCILIATION_DRIFT_ABOVE_MAXIMUM");

        return new(
            items.Count,
            matched,
            missing,
            workMismatch,
            snapshotMismatch,
            authorityViolation,
            duplicateWithoutOutput,
            drift,
            drift <= options.MaximumReconciliationDrift,
            reasons,
            false,
            false,
            observedAtUtc);
    }
}

public sealed record OptionChainRecoveryRequest(string TargetMode, string Reason);

public static class OptionChainRollbackEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainRollbackReconciliation(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/rollout/runtime-state", (
            OptionChainRolloutState state) => Results.Ok(state.Snapshot()));

        endpoints.MapPost("/api/v1/internal/option-chain/rollout/enforce", (
            OptionChainGuardrailEvaluator evaluator,
            OptionChainExecutionState executionState,
            OptionChainRollbackOptions options,
            OptionChainRolloutState state) =>
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            var evaluation = evaluator.Evaluate(executionState.Snapshot(observedAtUtc), observedAtUtc);
            if (!evaluation.RollbackRequired || !options.AutomaticRollbackEnabled)
                return Results.Ok(new { enforced = false, evaluation, state = state.Snapshot() });

            var reason = string.Join(",", evaluation.Breaches);
            return Results.Ok(new { enforced = true, evaluation, state = state.Rollback(reason, observedAtUtc) });
        });

        endpoints.MapPost("/api/v1/internal/option-chain/rollout/recover", (
            OptionChainRecoveryRequest request,
            OptionChainRollbackOptions options,
            OptionChainRolloutState state) =>
        {
            if (!options.ExplicitRecoveryRequired)
                return Results.Conflict(new { outcome = "EXPLICIT_RECOVERY_NOT_CONFIGURED" });
            return Results.Ok(state.Recover(request.TargetMode, request.Reason, DateTimeOffset.UtcNow));
        });

        endpoints.MapPost("/api/v1/internal/option-chain/reconciliation/evaluate", (
            IReadOnlyCollection<OptionChainReconciliationItem> items,
            OptionChainEvidenceReconciler reconciler,
            OptionChainRolloutState state) =>
        {
            var report = reconciler.Reconcile(items, DateTimeOffset.UtcNow);
            if (report.Healthy)
                return Results.Ok(new { report, state = state.Snapshot() });

            return Results.Json(
                new { report, state = state.Rollback("RECONCILIATION_DRIFT", DateTimeOffset.UtcNow) },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return endpoints;
    }
}

public static class OptionChainRollbackRegistration
{
    public static IServiceCollection AddOptionChainRollbackReconciliation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection("OptionChainRollback").Get<OptionChainRollbackOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<OptionChainRolloutState>();
        services.AddSingleton<OptionChainEvidenceReconciler>();
        return services;
    }
}
