using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record ConfiguredMarketDataSubscriptionOptions
{
    public string ProviderCode { get; init; } = "UPSTOX";
    public string FeedMode { get; init; } = "full";
    public string RecoveryTimeframe { get; init; } = "5m";
    public string[] InstrumentKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<MarketDataSubscriptionItem> GetItems() =>
        InstrumentKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select((key, index) => new MarketDataSubscriptionItem(
                key.Trim(),
                FeedMode,
                RecoveryTimeframe,
                index + 1))
            .DistinctBy(item => item.ProviderInstrumentKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed class ConfiguredMarketDataSubscriptionCatalog(
    ConfiguredMarketDataSubscriptionOptions options,
    TimeProvider timeProvider) : IMarketDataSubscriptionCatalog
{
    public Task<MarketDataSubscriptionPlan> GetPlanAsync(
        string providerCode,
        string feedMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = options.GetItems()
            .Where(item => item.FeedMode.Equals(
                feedMode,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult(BuildPlan(
            providerCode,
            feedMode,
            items,
            timeProvider.GetUtcNow()));
    }

    internal static MarketDataSubscriptionPlan BuildPlan(
        string providerCode,
        string feedMode,
        IReadOnlyCollection<MarketDataSubscriptionItem> items,
        DateTimeOffset generatedAtUtc)
    {
        var versionInput = string.Join(
            '|',
            items.OrderBy(item => item.Priority)
                .ThenBy(item => item.ProviderInstrumentKey)
                .Select(item =>
                    $"{item.ProviderInstrumentKey}:{item.FeedMode}:" +
                    $"{item.RecoveryTimeframe}:{item.Priority}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(versionInput));
        return new MarketDataSubscriptionPlan(
            providerCode,
            feedMode,
            items,
            Convert.ToHexString(hash[..8]),
            generatedAtUtc);
    }
}

public sealed class SqlServerMarketDataSubscriptionCatalog(
    SqlServerMarketDataOptions options,
    TimeProvider timeProvider) : IMarketDataSubscriptionCatalog
{
    public async Task<MarketDataSubscriptionPlan> GetPlanAsync(
        string providerCode,
        string feedMode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                mapping.[broker_instrument_key],
                subscription.[feed_mode],
                subscription.[recovery_timeframe],
                subscription.[priority]
            FROM [market].[live_feed_subscriptions] subscription
            INNER JOIN [reference].[broker_instrument_mappings] mapping
                ON mapping.[broker_instrument_mapping_id] =
                   subscription.[broker_instrument_mapping_id]
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = mapping.[instrument_id]
            WHERE broker.[broker_code] = @provider_code
              AND broker.[is_active] = 1
              AND mapping.[is_active] = 1
              AND mapping.[valid_to_date] IS NULL
              AND instrument.[status] = 'ACTIVE'
              AND subscription.[feed_mode] = @feed_mode
              AND subscription.[is_enabled] = 1
              AND subscription.[valid_from_utc] <= SYSUTCDATETIME()
              AND
              (
                  subscription.[valid_to_utc] IS NULL
                  OR subscription.[valid_to_utc] > SYSUTCDATETIME()
              )
            ORDER BY subscription.[priority], mapping.[broker_instrument_key];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@provider_code", SqlDbType.VarChar, 30).Value =
            providerCode;
        command.Parameters.Add("@feed_mode", SqlDbType.VarChar, 30).Value = feedMode;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<MarketDataSubscriptionItem>();

        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MarketDataSubscriptionItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return ConfiguredMarketDataSubscriptionCatalog.BuildPlan(
            providerCode,
            feedMode,
            items,
            timeProvider.GetUtcNow());
    }
}
