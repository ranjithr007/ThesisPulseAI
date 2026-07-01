using System.Data;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<IReadOnlyCollection<DerivativeExpiryReferenceV1>> GetExpiriesAsync(
        string underlyingProviderInstrumentKey,
        string? marketSegment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        var normalizedSegment = marketSegment?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedSegment) &&
            normalizedSegment is not ("FUTURES" or "OPTIONS"))
        {
            throw new ArgumentOutOfRangeException(nameof(marketSegment));
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var underlying = await ResolveInstrumentAsync(
            connection,
            transaction: null,
            underlyingProviderInstrumentKey,
            cancellationToken);
        const string sql = """
            SELECT
                schedule.[derivative_expiry_schedule_uid],
                underlying.[instrument_uid], underlying.[canonical_symbol],
                exchange.[exchange_code], schedule.[market_segment],
                schedule.[expiry_date], schedule.[expiry_type],
                schedule.[last_trading_date], schedule.[settlement_date],
                schedule.[rollover_start_date], schedule.[status],
                schedule.[calendar_version], schedule.[valid_from_date],
                schedule.[valid_to_date]
            FROM [reference].[derivative_expiry_schedules] schedule
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = schedule.[underlying_instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = schedule.[exchange_id]
            WHERE schedule.[underlying_instrument_id] = @underlying_id
              AND schedule.[valid_to_date] IS NULL
              AND (@segment IS NULL OR schedule.[market_segment] = @segment)
            ORDER BY schedule.[expiry_date], schedule.[market_segment];
            """;
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        command.Parameters.Add("@segment", SqlDbType.VarChar, 20).Value =
            DbValue(normalizedSegment);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<DerivativeExpiryReferenceV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DerivativeExpiryReferenceV1(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                DateOnly.FromDateTime(reader.GetDateTime(5)), reader.GetString(6),
                DateOnly.FromDateTime(reader.GetDateTime(7)),
                reader.IsDBNull(8) ? null : DateOnly.FromDateTime(reader.GetDateTime(8)),
                reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
                reader.GetString(10), reader.GetString(11),
                DateOnly.FromDateTime(reader.GetDateTime(12)),
                reader.IsDBNull(13) ? null : DateOnly.FromDateTime(reader.GetDateTime(13))));
        }

        return results;
    }
}
