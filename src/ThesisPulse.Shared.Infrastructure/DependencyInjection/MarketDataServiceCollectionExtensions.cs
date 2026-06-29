using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        var provider = configuration["MarketData:Persistence:Provider"] ?? "InMemory";

        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemoryMarketDataStore>();
            services.AddSingleton<IInstrumentCatalogStore>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryMarketDataStore>());
            services.AddSingleton<IMarketDataStore>(serviceProvider =>
                serviceProvider.GetRequiredService<InMemoryMarketDataStore>());
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
        return services;
    }
}
