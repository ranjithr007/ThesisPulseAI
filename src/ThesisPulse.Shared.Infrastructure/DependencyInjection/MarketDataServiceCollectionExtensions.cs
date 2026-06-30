using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Shared.Infrastructure.DependencyInjection;

public static class MarketDataServiceCollectionExtensions
{
    public static IServiceCollection AddThesisPulseMarketDataPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.TryAddSingleton(TimeProvider.System);

        var freshnessOptions = new MarketDataFreshnessOptions
        {
            PolicyVersion = configuration["MarketData:Freshness:PolicyVersion"]
                ?? "market-freshness-v1.0.0",
            LiveQuoteMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:LiveQuoteMaximumAgeMs", 5_000)),
            OneMinuteMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:OneMinuteMaximumAgeMs", 90_000)),
            FiveMinuteMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:FiveMinuteMaximumAgeMs", 420_000)),
            FifteenMinuteMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:FifteenMinuteMaximumAgeMs", 1_200_000)),
            OneHourMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:OneHourMaximumAgeMs", 4_500_000)),
            OneDayMaximumAge = TimeSpan.FromMilliseconds(
                configuration.GetValue("MarketData:Freshness:OneDayMaximumAgeMs", 129_600_000)),
            ExitAgeMultiplier = configuration.GetValue(
                "MarketData:Freshness:ExitAgeMultiplier",
                5m),
        };
        freshnessOptions.Validate();
        services.AddSingleton(freshnessOptions);
        services.AddSingleton<IMarketDataFreshnessEvaluator, MarketDataFreshnessEvaluator>();

        var recoveryOptions = new MarketDataRecoveryOptions
        {
            Enabled = configuration.GetValue("MarketData:Recovery:Enabled", false),
            PollIntervalSeconds = configuration.GetValue(
                "MarketData:Recovery:PollIntervalSeconds",
                60),
            GracePeriodSeconds = configuration.GetValue(
                "MarketData:Recovery:GracePeriodSeconds",
                45),
            MaximumGapCandles = configuration.GetValue(
                "MarketData:Recovery:MaximumGapCandles",
                500),
            MaximumRecoveryDays = configuration.GetValue(
                "MarketData:Recovery:MaximumRecoveryDays",
                30),
            SessionOpenIndia = TimeOnly.Parse(
                configuration["MarketData:Recovery:SessionOpenIndia"] ?? "09:15"),
            SessionCloseIndia = TimeOnly.Parse(
                configuration["MarketData:Recovery:SessionCloseIndia"] ?? "15:30"),
        };
        recoveryOptions.Validate();
        services.AddSingleton(recoveryOptions);
        services.AddSingleton<IMarketDataGapDetector, MarketDataGapDetector>();

        var configuredSubscriptions = new ConfiguredMarketDataSubscriptionOptions
        {
            ProviderCode = configuration["Upstox:ProviderCode"] ?? "UPSTOX",
            FeedMode = configuration["Upstox:LiveFeed:Mode"] ?? "full",
            RecoveryTimeframe = configuration["MarketData:Recovery:Timeframe"] ?? "5m",
            InstrumentKeys = configuration
                .GetSection("Upstox:LiveFeed:InstrumentKeys")
                .GetChildren()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray(),
        };
        services.AddSingleton(configuredSubscriptions);

        var provider = configuration["MarketData:Persistence:Provider"] ?? "InMemory";

        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryMarketDataStore>();
            services.AddSingleton<IInstrumentCatalogStore>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryMarketDataStore>());
            services.AddSingleton<IMarketDataStore>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryMarketDataStore>());
            services.AddSingleton<IMarketDataSubscriptionCatalog,
                ConfiguredMarketDataSubscriptionCatalog>();
            services.AddSingleton<IMarketDataRecoveryStateStore,
                InMemoryMarketDataRecoveryStateStore>();
            return services;
        }

        if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported market-data persistence provider '{provider}'.");
        }

        var connectionString = configuration.GetConnectionString("OperationalDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:OperationalDatabase is required when " +
                "MarketData:Persistence:Provider is SqlServer.");
        }

        var sqlOptions = new SqlServerMarketDataOptions
        {
            ConnectionString = connectionString,
            BrokerCode = configuration["MarketData:Persistence:BrokerCode"] ?? "UPSTOX",
            HistoricalSourceCode =
                configuration["MarketData:Persistence:HistoricalSourceCode"]
                ?? "UPSTOX_REST",
            LiveSourceCode = configuration["MarketData:Persistence:LiveSourceCode"]
                ?? "UPSTOX_WS",
            Actor = configuration["MarketData:Persistence:Actor"] ?? serviceName,
            CommandTimeoutSeconds = configuration.GetValue(
                "MarketData:Persistence:CommandTimeoutSeconds",
                30),
        };
        sqlOptions.Validate();
        services.AddSingleton(sqlOptions);
        services.AddSingleton<IInstrumentCatalogStore, SqlServerInstrumentCatalogStore>();
        services.AddSingleton<IMarketDataStore, SqlServerMarketDataStore>();
        services.AddSingleton<IMarketDataSubscriptionCatalog,
            SqlServerMarketDataSubscriptionCatalog>();
        services.AddSingleton<IMarketDataRecoveryStateStore,
            SqlServerMarketDataRecoveryStateStore>();
        return services;
    }
}
