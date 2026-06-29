namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed record SqlServerSignalStoreOptions
{
    public required string ConnectionString { get; init; }

    public required string CreatorEngineCode { get; init; }

    public string Actor { get; init; } = "ThesisPulse.Signal.Service";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(CreatorEngineCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}
