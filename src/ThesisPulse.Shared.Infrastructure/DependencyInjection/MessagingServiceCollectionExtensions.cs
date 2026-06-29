using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThesisPulse.Shared.Infrastructure.Caching;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.Shared.Infrastructure.DependencyInjection;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddThesisPulseMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IEventBus, InMemoryEventBus>();
        services.TryAddSingleton<IDistributedCacheProvider, InMemoryDistributedCacheProvider>();
        services.TryAddSingleton<InMemoryDispatchTarget>();
        services.TryAddSingleton<IDispatchTarget>(provider =>
            provider.GetRequiredService<InMemoryDispatchTarget>());
        services.TryAddSingleton<InboxMessageProcessor>();
        services.TryAddSingleton<OutboxDispatcher>();

        var providerName = configuration["Messaging:Provider"] ?? "InMemory";

        if (providerName.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            RegisterSqlServerStores(services, configuration, serviceName);
            return services;
        }

        if (!providerName.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported messaging provider '{providerName}'.");
        }

        services.TryAddSingleton<IInboxStore, InMemoryInboxStore>();
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();
        return services;
    }

    private static void RegisterSqlServerStores(
        IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var connectionString = configuration.GetConnectionString("OperationalDatabase");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:OperationalDatabase is required when " +
                "Messaging:Provider is SqlServer.");
        }

        var options = new SqlServerMessagingOptions
        {
            ConnectionString = connectionString,
            InstanceName = configuration["Messaging:InstanceName"] ?? serviceName,
            Actor = configuration["Messaging:Actor"] ?? serviceName,
            MaxAttempts = configuration.GetValue("Messaging:MaxAttempts", 5),
            LeaseDuration = TimeSpan.FromSeconds(
                configuration.GetValue("Messaging:LeaseSeconds", 60)),
            CommandTimeoutSeconds = configuration.GetValue(
                "Messaging:CommandTimeoutSeconds",
                30),
        };

        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IInboxStore, SqlServerInboxStore>();
        services.AddSingleton<IOutboxStore, SqlServerOutboxStore>();
    }
}
