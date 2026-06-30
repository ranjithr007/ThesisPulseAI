using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.MarketData.Service;

public static class MarketDataPublicationRegistration
{
    public static IServiceCollection AddMarketDataPublicationTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new MarketDataDispatchOptions
        {
            Enabled = configuration.GetValue("MarketData:Publication:DispatchEnabled", false),
            InternalApiKey = configuration["MarketData:Publication:InternalApiKey"],
            SignalServiceBaseUrl = CreateOptionalUri(
                configuration["MarketData:Publication:SignalServiceBaseUrl"]),
            TradingApiBaseUrl = CreateOptionalUri(
                configuration["MarketData:Publication:TradingApiBaseUrl"]),
            AiFeatureFactoryEnabled = configuration.GetValue(
                "MarketData:Publication:AiFeatureFactoryEnabled", false),
            AiOrderFlowEnabled = configuration.GetValue(
                "MarketData:Publication:AiOrderFlowEnabled", false),
            AiServiceBaseUrl = CreateOptionalUri(
                configuration["MarketData:Publication:AiServiceBaseUrl"]),
            AutomaticPaperWorkflowEnabled = configuration.GetValue(
                "MarketData:Publication:AutomaticPaperWorkflowEnabled", false),
            OperationsServiceBaseUrl = CreateOptionalUri(
                configuration["MarketData:Publication:OperationsServiceBaseUrl"]),
            BatchSize = configuration.GetValue(
                "MarketData:Publication:BatchSize", 100),
            PollIntervalMilliseconds = configuration.GetValue(
                "MarketData:Publication:PollIntervalMilliseconds", 1000),
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddHttpClient("SignalServiceMarketData");
        services.AddHttpClient("TradingApiMarketData");
        services.AddHttpClient("AiFeatureFactoryMarketData");
        services.AddHttpClient("AiOrderFlowMarketData");
        services.AddHttpClient("OperationsAutomaticPaperWorkflow");
        services.AddSingleton<MarketDataFanoutClient>();

        var provider = configuration["Messaging:Provider"] ?? "InMemory";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IMarketDataPendingPublicationStore,
                SqlServerMarketDataPendingPublicationStore>();
        }
        else
        {
            services.AddSingleton<IMarketDataPendingPublicationStore,
                InMemoryMarketDataPendingPublicationStore>();
        }

        if (options.Enabled)
        {
            services.AddHostedService<MarketDataPublicationDispatchWorker>();
        }

        return services;
    }

    private static Uri? CreateOptionalUri(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new Uri(value, UriKind.Absolute);
}
