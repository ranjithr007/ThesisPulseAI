using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

public sealed class ConfigurationUpstoxSubscriptionProvider(
    UpstoxLiveFeedOptions options) : IUpstoxSubscriptionProvider
{
    public ValueTask<UpstoxSubscriptionSnapshot> GetSubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = options.GetNormalizedInstrumentKeys();
        var versionInput = $"{options.Mode}|{string.Join('|', keys)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(versionInput));
        var version = Convert.ToHexString(hash[..8]);

        return ValueTask.FromResult(new UpstoxSubscriptionSnapshot(
            options.Mode.ToLowerInvariant(),
            keys,
            version));
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
