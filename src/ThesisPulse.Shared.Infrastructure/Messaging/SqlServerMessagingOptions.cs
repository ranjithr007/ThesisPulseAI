namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed record SqlServerMessagingOptions
{
    public required string ConnectionString { get; init; }

    public required string InstanceName { get; init; }

    public string Actor { get; init; } = "ThesisPulseAI.Platform";

    public int MaxAttempts { get; init; } = 5;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts),
                "Maximum attempts must be at least one.");
        }

        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                "Lease duration must be greater than zero.");
        }

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeoutSeconds),
                "Command timeout must be at least one second.");
        }
    }
}
