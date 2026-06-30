using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public static class EventConsumerRegistration
{
    public static IServiceCollection AddEventConsumerState(
        this IServiceCollection services)
    {
        services.AddSingleton<MarketDataConsumerBuffer>();
        services.TryAddSingleton<IMarketDataConsumerSink>(provider =>
            provider.GetRequiredService<MarketDataConsumerBuffer>());
        return services;
    }
}
