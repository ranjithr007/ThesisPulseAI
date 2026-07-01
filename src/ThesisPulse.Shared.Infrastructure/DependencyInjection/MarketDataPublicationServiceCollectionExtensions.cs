using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Shared.Infrastructure.DependencyInjection;

public static class MarketDataPublicationServiceCollectionExtensions
{
    public static IServiceCollection AddThesisPulseMarketDataPublication(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var options = new MarketDataPublicationOptions
        {
            Enabled = configuration.GetValue("MarketData:Publication:Enabled", false),
            OptionChainEnabled = configuration.GetValue(
                "MarketData:Publication:OptionChainEnabled",
                false),
            Environment = configuration["Platform:Environment"] ?? "PAPER",
            Producer = serviceName,
            ProducerVersion = configuration["MarketData:Publication:ProducerVersion"]
                ?? "0.4.0",
            ConfigurationVersion = configuration["Platform:ConfigurationVersion"]
                ?? "platform-foundation-v1.0.0",
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<MarketDataPublicationFactory>();
        services.AddSingleton<IMarketDataPublicationWriter,
            MarketDataPublicationWriter>();

        var provider = configuration["Messaging:Provider"] ?? "InMemory";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IMarketDataReplayStore,
                SqlServerMarketDataReplayStore>();
            services.AddSingleton<IMarketDataConsumerCheckpointStore,
                SqlServerMarketDataConsumerCheckpointStore>();
        }
        else
        {
            services.AddSingleton<IMarketDataReplayStore,
                InMemoryMarketDataReplayStore>();
            services.AddSingleton<IMarketDataConsumerCheckpointStore,
                InMemoryMarketDataConsumerCheckpointStore>();
        }

        return services;
    }
}
