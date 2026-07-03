namespace ThesisPulse.Signal.Service;

public sealed record SqlServerOptionChainWorkQueueOptions
{
    public required string ConnectionString { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
    }
}
