using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Shared.Infrastructure.DependencyInjection;

public static class SignalStoreServiceCollectionExtensions
{
    public static IServiceCollection AddThesisPulseSignalStore(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var provider = configuration["SignalPersistence:Provider"] ?? "InMemory";

        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemorySignalStore>();
            services.AddSingleton<ISignalStore>(provider =>
                provider.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<ISignalStatusStore>(provider =>
                provider.GetRequiredService<InMemorySignalStore>());
            return services;
        }

        if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported signal persistence provider '{provider}'.");
        }

        var connectionString = configuration.GetConnectionString("OperationalDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:OperationalDatabase is required when " +
                "SignalPersistence:Provider is SqlServer.");
        }

        var options = new SqlServerSignalStoreOptions
        {
            ConnectionString = connectionString,
            CreatorEngineCode = configuration["SignalPersistence:CreatorEngineCode"]
                ?? "THESIS_PULSE_MOCK_FUSION",
            Actor = configuration["SignalPersistence:Actor"] ?? serviceName,
            CommandTimeoutSeconds = configuration.GetValue(
                "SignalPersistence:CommandTimeoutSeconds",
                30),
        };

        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<ISignalStore, SqlServerSignalStore>();
        services.AddSingleton<ISignalStatusStore, SqlServerSignalStatusStore>();
        return services;
    }
}
