namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record SqlServerMarketDataOptions
{
    public required string ConnectionString { get; init; }

    public string BrokerCode { get; init; } = "UPSTOX";

    public string HistoricalSourceCode { get; init; } = "UPSTOX_REST";

    public string LiveSourceCode { get; init; } = "UPSTOX_WS";

    public string Actor { get; init; } = "ThesisPulse.MarketData.Service";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(HistoricalSourceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(LiveSourceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}
