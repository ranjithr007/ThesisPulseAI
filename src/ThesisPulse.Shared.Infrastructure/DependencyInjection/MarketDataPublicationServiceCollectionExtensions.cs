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
            Environment = configuration["Platform:Environment"] ?? "PAPER",
            Producer = serviceName,
            ProducerVersion = "0.4.0",
            ConfigurationVersion = configuration["Platform:ConfigurationVersion"] ?? "platform-foundation-v1.0.0",
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<MarketDataPublicationFactory>();
        services.AddSingleton<IMarketDataPublicationWriter, MarketDataPublicationWriter>();
        return services;
    }
}
