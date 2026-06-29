using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public static class UpstoxServiceCollectionExtensions
{
    public static IServiceCollection AddUpstoxMarketDataAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var apiBaseUrl = configuration["Upstox:ApiBaseUrl"]
            ?? "https://api.upstox.com";
        var instrumentMasterUrl = configuration["Upstox:InstrumentMasterUrl"]
            ?? "https://assets.upstox.com/market-quote/instruments/exchange/NSE.json.gz";

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiBaseUri))
        {
            throw new InvalidOperationException("Upstox API base URL must be absolute.");
        }

        if (!Uri.TryCreate(
                instrumentMasterUrl,
                UriKind.Absolute,
                out var instrumentMasterUri))
        {
            throw new InvalidOperationException(
                "Upstox instrument master URL must be absolute.");
        }

        var options = new UpstoxMarketDataOptions
        {
            Enabled = configuration.GetValue("Upstox:Enabled", false),
            ApiBaseUrl = apiBaseUri,
            InstrumentMasterUrl = instrumentMasterUri,
            HistoricalPathTemplate = configuration["Upstox:HistoricalPathTemplate"]
                ?? "/v3/historical-candle/{instrumentKey}/{unit}/{interval}/{toDate}/{fromDate}",
            MarketFeedAuthorizePath = configuration["Upstox:MarketFeedAuthorizePath"]
                ?? "/v3/feed/market-data-feed/authorize",
            AccessToken = configuration["Upstox:AccessToken"],
            TickSizeDivisor = configuration.GetValue("Upstox:TickSizeDivisor", 100m),
            RequestTimeoutSeconds = configuration.GetValue(
                "Upstox:RequestTimeoutSeconds",
                30),
            MaximumInstrumentCount = configuration.GetValue(
                "Upstox:MaximumInstrumentCount",
                500_000),
        };
        options.Validate();

        var instrumentKeys = configuration
            .GetSection("Upstox:LiveFeed:InstrumentKeys")
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        var liveFeedOptions = new UpstoxLiveFeedOptions
        {
            Enabled = configuration.GetValue("Upstox:LiveFeed:Enabled", false),
            Mode = configuration["Upstox:LiveFeed:Mode"]
                ?? UpstoxLiveFeedModes.Full,
            InstrumentKeys = instrumentKeys,
            ConnectTimeoutSeconds = configuration.GetValue(
                "Upstox:LiveFeed:ConnectTimeoutSeconds",
                20),
            MessageSilenceTimeoutSeconds = configuration.GetValue(
                "Upstox:LiveFeed:MessageSilenceTimeoutSeconds",
                45),
            ClosedMarketMessageSilenceTimeoutSeconds = configuration.GetValue(
                "Upstox:LiveFeed:ClosedMarketMessageSilenceTimeoutSeconds",
                300),
            KeepAliveSeconds = configuration.GetValue(
                "Upstox:LiveFeed:KeepAliveSeconds",
                20),
            InitialReconnectDelayMilliseconds = configuration.GetValue(
                "Upstox:LiveFeed:InitialReconnectDelayMilliseconds",
                1_000),
            MaximumReconnectDelayMilliseconds = configuration.GetValue(
                "Upstox:LiveFeed:MaximumReconnectDelayMilliseconds",
                60_000),
            StableConnectionResetSeconds = configuration.GetValue(
                "Upstox:LiveFeed:StableConnectionResetSeconds",
                60),
            ReceiveBufferBytes = configuration.GetValue(
                "Upstox:LiveFeed:ReceiveBufferBytes",
                64 * 1024),
            MaximumMessageBytes = configuration.GetValue(
                "Upstox:LiveFeed:MaximumMessageBytes",
                4 * 1024 * 1024),
        };
        liveFeedOptions.Validate(options.Enabled);

        services.AddSingleton(options);
        services.AddSingleton(liveFeedOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IUpstoxCredentialProvider, ConfigurationUpstoxCredentialProvider>();
        services.AddSingleton<IUpstoxLiveFeedNormalizer, UpstoxLiveFeedNormalizer>();
        services.AddSingleton<IUpstoxMarketDataFeedDecoder, UpstoxMarketDataFeedDecoder>();
        services.AddSingleton<IUpstoxSubscriptionProvider, ConfigurationUpstoxSubscriptionProvider>();
        services.AddSingleton<IUpstoxSubscriptionCommandBuilder, UpstoxSubscriptionCommandBuilder>();
        services.AddSingleton<IUpstoxWebSocketConnectionFactory, UpstoxWebSocketConnectionFactory>();
        services.AddSingleton<UpstoxLiveFeedHealthState>();
        services.AddSingleton<IUpstoxLiveFeedHealthState>(serviceProvider =>
            serviceProvider.GetRequiredService<UpstoxLiveFeedHealthState>());
        services.AddHttpClient<IMarketDataProvider, UpstoxMarketDataProvider>(client =>
        {
            client.BaseAddress = apiBaseUri;
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ThesisPulseAI-MarketData/0.2.0");
        });

        if (liveFeedOptions.Enabled)
        {
            services.AddHostedService<UpstoxMarketDataFeedWorker>();
        }

        return services;
    }
}
