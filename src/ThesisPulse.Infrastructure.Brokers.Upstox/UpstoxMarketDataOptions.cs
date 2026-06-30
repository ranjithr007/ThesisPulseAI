namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxMarketDataOptions
{
    public bool Enabled { get; init; }

    public Uri ApiBaseUrl { get; init; } = new("https://api.upstox.com");

    public Uri InstrumentMasterUrl { get; init; } = new(
        "https://assets.upstox.com/market-quote/instruments/exchange/NSE.json.gz");

    public string HistoricalPathTemplate { get; init; } =
        "/v3/historical-candle/{instrumentKey}/{unit}/{interval}/{toDate}/{fromDate}";

    public string MarketFeedAuthorizePath { get; init; } =
        "/v3/feed/market-data-feed/authorize";

    public string? AccessToken { get; init; }

    public decimal TickSizeDivisor { get; init; } = 100m;

    public int RequestTimeoutSeconds { get; init; } = 30;

    public int MaximumInstrumentCount { get; init; } = 500_000;

    public void Validate()
    {
        if (!ApiBaseUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Upstox API base URL must be absolute.");
        }

        if (!InstrumentMasterUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "Upstox instrument master URL must be absolute.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(HistoricalPathTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(MarketFeedAuthorizePath);

        if (TickSizeDivisor <= 0)
        {
            throw new InvalidOperationException(
                "Upstox tick-size divisor must be greater than zero.");
        }

        if (RequestTimeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                "Upstox request timeout must be at least one second.");
        }

        if (MaximumInstrumentCount < 1)
        {
            throw new InvalidOperationException(
                "Upstox maximum instrument count must be greater than zero.");
        }
    }
}

public interface IUpstoxCredentialProvider
{
    ValueTask<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default);
}

public sealed class ConfigurationUpstoxCredentialProvider(
    UpstoxMarketDataOptions options) : IUpstoxCredentialProvider
{
    public ValueTask<string> GetAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.AccessToken))
        {
            throw new InvalidOperationException(
                "Upstox access token is not configured. Supply it through user " +
                "secrets or the runtime secret provider.");
        }

        return ValueTask.FromResult(options.AccessToken);
    }
}
