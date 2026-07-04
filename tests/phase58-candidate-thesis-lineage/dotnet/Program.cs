var failures = new List<string>();
var root = FindRepositoryRoot();

Run("V0029 preserves mutually exclusive thesis lineage", () =>
{
    var migration = Read("database/migrations/V0029__bridge_candidate_thesis_to_trade_plans.sql");
    Require(migration, "ADD [candidate_thesis_uid] uniqueidentifier NULL");
    Require(migration, "ALTER COLUMN [thesis_id] bigint NULL");
    Require(migration, "[thesis_id] IS NOT NULL AND [candidate_thesis_uid] IS NULL");
    Require(migration, "[thesis_id] IS NULL AND [candidate_thesis_uid] IS NOT NULL");
    Require(migration, "ux_trade_plans_candidate_thesis_version");
    Require(migration, "ux_trade_plans_current_candidate_thesis");
    Require(migration, "schema_baseline_version] = 'V0029'");
});

Run("V0029 verification checks schema and data invariants", () =>
{
    var verification = Read("database/verification/V0029__verify_candidate_thesis_trade_plan_bridge.sql");
    Require(verification, "ck_trade_plans_thesis_lineage");
    Require(verification, "ux_trade_plans_candidate_thesis_version");
    Require(verification, "ux_trade_plans_current_candidate_thesis");
    Require(verification, "V0029_VERIFIED");
});

Run("automatic trade-plan persistence uses authoritative fusion lineage", () =>
{
    var source = Read("src/ThesisPulse.Risk.Service/SqlServerAutomaticTradePlanResultStore.cs");
    Require(source, "[intelligence].[signal_fusion_lineage]");
    Require(source, "lineage.[thesis_uid] = @thesis_uid");
    Require(source, "e.[correlation_id] = @correlation_id");
    Require(source, "s.[correlation_id] = @correlation_id");
    Require(source, "[candidate_thesis_uid]");
    Require(source, "thesisLineageSource = \"intelligence.signal_fusion_lineage\"");
    Reject(source, "INSERT INTO [thesis].[theses]");
});

Run("automatic trade-plan persistence rejects command/result mismatches", () =>
{
    var source = Read("src/ThesisPulse.Risk.Service/SqlServerAutomaticTradePlanResultStore.cs");
    Require(source, "plan.RiskDecisionUid != command.RiskDecisionUid");
    Require(source, "plan.SignalUid != command.SignalUid");
    Require(source, "plan.ThesisUid != command.ThesisUid");
    Require(source, "READY Trade Plan correlation ID does not match its command");
});

Run("execution authorization validates candidate thesis against its signal", () =>
{
    var source = Read("src/ThesisPulse.Shared.Infrastructure/Execution/SqlServerPaperExecutionLedgerStore.cs");
    Require(source, "candidate_lineage.[signal_id] = tp.[signal_id]");
    Require(source, "candidate_lineage.[thesis_uid] = tp.[candidate_thesis_uid]");
    Require(source, "candidate_lineage.[thesis_uid] = @thesis_uid");
    Require(source, "tp.[correlation_id] = @correlation_id");
    Require(source, "COALESCE(th.[thesis_uid], candidate_lineage.[thesis_uid])");
    Reject(source, "INSERT INTO [thesis].[theses]");
});

Run("lifecycle read model exposes candidate thesis without partial lineage", () =>
{
    var source = Read("src/ThesisPulse.Execution.Service/PaperTradeLifecycleReadModel.cs");
    Require(source, "COALESCE(t.[thesis_uid], candidate_lineage.[thesis_uid])");
    Require(source, "THEN 'CANDIDATE'");
    Require(source, "candidate_lineage.[created_at_utc]");
});

Run("acceptance evidence reports modern and legacy source tables", () =>
{
    var source = Read("src/ThesisPulse.Execution.Service/PaperTradeLifecycleAcceptanceStore.cs");
    Require(source, "'intelligence.signal_fusion_lineage'");
    Require(source, "'risk.signal_risk_evaluations'");
    Require(source, "'thesis.theses'");
    Require(source, "'risk.risk_decisions'");
    Require(source, "r.candidate_thesis_uid = lineage.thesis_uid");
    Require(source, "r.signal_risk_evaluation_id = sre.signal_risk_evaluation_id");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Phase 5.8 acceptance failed with {failures.Count} error(s):");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("Phase 5.8 candidate-thesis lineage checks passed.");
return 0;

string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

void Run(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void Require(string value, string fragment)
{
    if (!value.Contains(fragment, StringComparison.Ordinal))
        throw new InvalidOperationException($"Required fragment was not found: {fragment}");
}

static void Reject(string value, string fragment)
{
    if (value.Contains(fragment, StringComparison.Ordinal))
        throw new InvalidOperationException($"Forbidden fragment was found: {fragment}");
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "ThesisPulseAI.sln")))
            return directory.FullName;
        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the ThesisPulseAI repository root.");
}
