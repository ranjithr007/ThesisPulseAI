using System.Text.Json;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxSubscriptionSnapshot(
    string Mode,
    IReadOnlyCollection<string> InstrumentKeys,
    string Version);

public sealed record UpstoxSubscriptionCommand(
    string Guid,
    byte[] Payload);

public interface IUpstoxSubscriptionProvider
{
    ValueTask<UpstoxSubscriptionSnapshot> GetSubscriptionAsync(
        CancellationToken cancellationToken = default);
}

public interface IUpstoxSubscriptionCommandBuilder
{
    UpstoxSubscriptionCommand BuildSubscribe(UpstoxSubscriptionSnapshot subscription);
}

public sealed class CatalogUpstoxSubscriptionProvider(
    UpstoxLiveFeedOptions options,
    IMarketDataSubscriptionCatalog catalog) : IUpstoxSubscriptionProvider
{
    public async ValueTask<UpstoxSubscriptionSnapshot> GetSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = await catalog.GetPlanAsync(
            "UPSTOX",
            options.Mode,
            cancellationToken);
        var keys = plan.Items
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.ProviderInstrumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.ProviderInstrumentKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keys.Length == 0)
        {
            throw new InvalidOperationException(
                "The active Upstox subscription plan contains no instrument keys.");
        }

        return new UpstoxSubscriptionSnapshot(
            options.Mode.ToLowerInvariant(),
            keys,
            plan.Version);
    }
}

public sealed class UpstoxSubscriptionCommandBuilder :
    IUpstoxSubscriptionCommandBuilder
{
    public UpstoxSubscriptionCommand BuildSubscribe(
        UpstoxSubscriptionSnapshot subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!UpstoxLiveFeedModes.Supported.Contains(subscription.Mode))
        {
            throw new InvalidOperationException(
                $"Unsupported subscription mode '{subscription.Mode}'.");
        }

        if (subscription.InstrumentKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "An Upstox subscription requires at least one instrument key.");
        }

        var maximum = UpstoxLiveFeedModes.GetMaximumInstrumentCount(
            subscription.Mode);
        if (subscription.InstrumentKeys.Count > maximum)
        {
            throw new InvalidOperationException(
                $"Subscription mode '{subscription.Mode}' exceeds its {maximum}-key limit.");
        }

        var guid = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            guid,
            method = "sub",
            data = new
            {
                mode = subscription.Mode,
                instrumentKeys = subscription.InstrumentKeys,
            },
        });

        return new UpstoxSubscriptionCommand(guid, payload);
    }
}
