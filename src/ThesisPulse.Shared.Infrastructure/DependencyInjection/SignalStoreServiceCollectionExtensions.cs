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
            services.AddSingleton<ISignalStore>(p => p.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<IFusionSignalStore>(p => p.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<ISignalScannerStore>(p => p.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<ISignalStatusStore>(p => p.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<IDueSignalMaintenanceStore>(p => p.GetRequiredService<InMemorySignalStore>());
            services.AddSingleton<ISignalDecisionProjectionStore, InMemorySignalDecisionProjectionStore>();
            return services;
        }

        if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported signal persistence provider '{provider}'.");

        var connectionString = configuration.GetConnectionString("OperationalDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:OperationalDatabase is required when SignalPersistence:Provider is SqlServer.");

        var options = new SqlServerSignalStoreOptions
        {
            ConnectionString = connectionString,
            CreatorEngineCode = configuration["SignalPersistence:CreatorEngineCode"]
                ?? "THESIS_PULSE_THESIS_FUSION",
            Actor = configuration["SignalPersistence:Actor"] ?? serviceName,
            CommandTimeoutSeconds = configuration.GetValue("SignalPersistence:CommandTimeoutSeconds", 30),
        };
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<SqlServerSignalStore>();
        services.AddSingleton<ISignalStore>(p => p.GetRequiredService<SqlServerSignalStore>());
        services.AddSingleton<IFusionSignalStore>(p => p.GetRequiredService<SqlServerSignalStore>());
        services.AddSingleton<AuthoritativeRiskSignalScannerStore>();
        services.AddSingleton<AuthoritativeTradePlanSignalScannerStore>();
        services.AddSingleton<ISignalScannerStore>(p => p.GetRequiredService<AuthoritativeTradePlanSignalScannerStore>());
        services.AddSingleton<ISignalDecisionProjectionStore>(p => p.GetRequiredService<AuthoritativeTradePlanSignalScannerStore>());
        services.AddSingleton<ISignalStatusStore, SqlServerSignalStatusStore>();
        services.AddSingleton<IDueSignalMaintenanceStore, SqlServerDueSignalMaintenanceStore>();
        return services;
    }
}
