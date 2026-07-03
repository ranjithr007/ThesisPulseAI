namespace ThesisPulse.Signal.Service;

public sealed record OptionChainSqlRuntimeOptions
{
    public bool Enabled { get; init; }
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int LeaseSeconds { get; init; } = 120;
    public string InstanceName { get; init; } = Environment.MachineName;
}
