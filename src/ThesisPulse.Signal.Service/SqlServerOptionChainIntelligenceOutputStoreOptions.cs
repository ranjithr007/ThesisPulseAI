namespace ThesisPulse.Signal.Service;

public sealed record SqlServerOptionChainIntelligenceOutputStoreOptions
{
    public required string ConnectionString { get; init; }

    public string EngineCode { get; init; } = "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE";

    public string Environment { get; init; } = "PAPER";

    public string SourceService { get; init; } = "ThesisPulse.Signal.Service";

    public string SourceVersion { get; init; } = "1.0.0";

    public string Actor { get; init; } = "ThesisPulse.Signal.OptionChainPersistence";

    public TimeSpan OutputTimeToLive { get; init; } = TimeSpan.FromMinutes(2);

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(EngineCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceService);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);

        if (Environment is not ("PAPER" or "SHADOW" or "LIVE"))
            throw new ArgumentOutOfRangeException(nameof(Environment), "Environment must be PAPER, SHADOW, or LIVE.");

        if (OutputTimeToLive <= TimeSpan.Zero || OutputTimeToLive > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(OutputTimeToLive), "Output TTL must be greater than zero and no more than one hour.");

        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), "Command timeout must be between 1 and 300 seconds.");
    }
}
