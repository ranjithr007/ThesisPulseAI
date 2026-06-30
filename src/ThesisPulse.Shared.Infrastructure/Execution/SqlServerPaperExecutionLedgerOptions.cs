namespace ThesisPulse.Shared.Infrastructure.Execution;

public sealed record SqlServerPaperExecutionLedgerOptions
{
    public required string ConnectionString { get; init; }

    public string BrokerAccountReference { get; init; } = "PAPER-PRIMARY";

    public string Actor { get; init; } = "ThesisPulse.Execution.Service";

    public string SourceVersion { get; init; } = "1.0.0";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerAccountReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceVersion);

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}
