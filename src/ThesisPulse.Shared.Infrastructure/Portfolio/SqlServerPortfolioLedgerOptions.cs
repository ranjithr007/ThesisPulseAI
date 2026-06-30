namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed record SqlServerPortfolioLedgerOptions
{
    public required string ConnectionString { get; init; }

    public string Actor { get; init; } = "ThesisPulse.Portfolio.Service";

    public string SourceVersion { get; init; } = "1.0.0";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceVersion);

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}
