namespace ThesisPulse.Execution.Service;

public sealed class DeterministicPaperExecutionOptions
{
    public const string SectionName = "PaperExecution";

    public string GateVersion { get; init; } = "deterministic-paper-execution-v1.0.0";
    public string ExecutionPolicyVersion { get; init; } = "execution-policy-v1.0.0";
    public List<string> AllowedEnvironments { get; init; } = ["PAPER"];
    public int MaximumOperationalSnapshotAgeSeconds { get; init; } = 30;
    public int MaximumCommandValiditySeconds { get; init; } = 30;
}
