using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Shared.Infrastructure.DependencyInjection;

public static class MarketDataConsumerStateServiceCollectionExtensions
{
    public static IServiceCollection AddThesisPulseMarketDataConsumerState(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Messaging:Provider"] ?? "InMemory";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IMarketDataConsumerCheckpointStore,
                SqlServerMarketDataConsumerCheckpointStore>();
        }
        else
        {
            services.AddSingleton<IMarketDataConsumerCheckpointStore,
                InMemoryMarketDataConsumerCheckpointStore>();
        }

        return services;
    }
}
