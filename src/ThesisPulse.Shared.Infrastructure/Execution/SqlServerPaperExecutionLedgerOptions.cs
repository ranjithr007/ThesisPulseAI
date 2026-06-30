namespace ThesisPulse.Shared.Infrastructure.Execution;

public sealed record SqlServerPaperExecutionLedgerOptions
{
    public required string ConnectionString { get; init; }

    public string BrokerAccountReference { get; init; } = "PAPER-PRIMARY";

    public string CurrencyCode { get; init; } = "INR";

    public string Actor { get; init; } = "ThesisPulse.Execution.Service";

    public string SourceVersion { get; init; } = "1.0.0";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerAccountReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(CurrencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceVersion);

        if (CurrencyCode.Trim().Length != 3)
        {
            throw new ArgumentException("CurrencyCode must contain three characters.");
        }

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}
