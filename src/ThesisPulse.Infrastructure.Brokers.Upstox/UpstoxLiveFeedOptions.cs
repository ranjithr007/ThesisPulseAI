namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public static class UpstoxLiveFeedModes
{
    public const string Ltpc = "ltpc";
    public const string Full = "full";
    public const string OptionGreeks = "option_greeks";
    public const string FullD30 = "full_d30";

    public static readonly IReadOnlySet<string> Supported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Ltpc,
            Full,
            OptionGreeks,
            FullD30,
        };

    public static int GetMaximumInstrumentCount(string mode) =>
        mode.ToLowerInvariant() switch
        {
            Ltpc => 5_000,
            OptionGreeks => 3_000,
            Full => 2_000,
            FullD30 => 50,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
}

public sealed record UpstoxLiveFeedOptions
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = UpstoxLiveFeedModes.Full;
    public string[] InstrumentKeys { get; init; } = Array.Empty<string>();
    public int ConnectTimeoutSeconds { get; init; } = 20;
    public int MessageSilenceTimeoutSeconds { get; init; } = 45;
    public int ClosedMarketMessageSilenceTimeoutSeconds { get; init; } = 300;
    public int KeepAliveSeconds { get; init; } = 20;
    public int InitialReconnectDelayMilliseconds { get; init; } = 1_000;
    public int MaximumReconnectDelayMilliseconds { get; init; } = 60_000;
    public int StableConnectionResetSeconds { get; init; } = 60;
    public int ReceiveBufferBytes { get; init; } = 64 * 1024;
    public int MaximumMessageBytes { get; init; } = 4 * 1024 * 1024;

    public void Validate(bool providerEnabled)
    {
        if (!UpstoxLiveFeedModes.Supported.Contains(Mode))
        {
            throw new InvalidOperationException(
                $"Unsupported Upstox live-feed mode '{Mode}'.");
        }

        if (Enabled && !providerEnabled)
        {
            throw new InvalidOperationException(
                "Upstox must be enabled when its live feed is enabled.");
        }

        var keys = GetNormalizedInstrumentKeys();
        if (Enabled && keys.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one Upstox live-feed instrument key is required.");
        }

        var maximum = UpstoxLiveFeedModes.GetMaximumInstrumentCount(Mode);
        if (keys.Length > maximum)
        {
            throw new InvalidOperationException(
                $"Mode '{Mode}' supports at most {maximum} instrument keys.");
        }

        if (ConnectTimeoutSeconds is < 1 or > 120 ||
            MessageSilenceTimeoutSeconds is < 5 or > 600 ||
            ClosedMarketMessageSilenceTimeoutSeconds < MessageSilenceTimeoutSeconds ||
            KeepAliveSeconds is < 5 or > 120 ||
            InitialReconnectDelayMilliseconds < 100 ||
            MaximumReconnectDelayMilliseconds < InitialReconnectDelayMilliseconds ||
            StableConnectionResetSeconds < 5 ||
            ReceiveBufferBytes is < 4_096 or > 1_048_576 ||
            MaximumMessageBytes < ReceiveBufferBytes ||
            MaximumMessageBytes > 32 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "One or more Upstox live-feed resilience settings are invalid.");
        }
    }

    public string[] GetNormalizedInstrumentKeys() =>
        InstrumentKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
