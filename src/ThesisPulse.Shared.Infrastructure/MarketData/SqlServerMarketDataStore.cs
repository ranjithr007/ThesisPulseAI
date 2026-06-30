using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore : IMarketDataStore
{
    private readonly SqlServerMarketDataOptions _options;
    private readonly IMarketDataFreshnessEvaluator _freshnessEvaluator;

    public SqlServerMarketDataStore(
        SqlServerMarketDataOptions options,
        IMarketDataFreshnessEvaluator freshnessEvaluator,
        MarketDataPublicationFactory publicationFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(freshnessEvaluator);
        ArgumentNullException.ThrowIfNull(publicationFactory);
        options.Validate();
        _options = options;
        _freshnessEvaluator = freshnessEvaluator;
        _publicationFactory = publicationFactory;
    }

    public async Task<IReadOnlyCollection<StoredCandleV1>> GetLatestCandlesAsync(
        string instrumentKey,
        string timeframe,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentKey);

        if (!MarketDataContractV1.Timeframes.Contains(timeframe))
        {
            throw new ArgumentOutOfRangeException(nameof(timeframe));
        }

        if (maximumCount is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        const string sql = """
            SELECT TOP (@maximum_count)
                candle.[candle_id], candle.[candle_uid],
                mapping.[broker_instrument_key], candle.[timeframe],
                candle.[open_at_utc], candle.[close_at_utc],
                candle.[open_price], candle.[high_price], candle.[low_price],
                candle.[close_price], candle.[volume_qty],
                candle.[quality_status], candle.[is_usable_for_new_exposure],
                candle.[received_at_utc]
            FROM [market].[candles] candle
            INNER JOIN [reference].[broker_instrument_mappings] mapping
                ON mapping.[instrument_id] = candle.[instrument_id]
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            WHERE broker.[broker_code] = @broker_code
              AND mapping.[broker_instrument_key] = @instrument_key
              AND mapping.[is_active] = 1
              AND candle.[timeframe] = @timeframe
              AND candle.[is_current] = 1
            ORDER BY candle.[open_at_utc] DESC, candle.[revision] DESC;
            """;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            _options.BrokerCode;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            instrumentKey;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value = timeframe;

        var candles = new List<StoredCandleV1>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            candles.Add(new StoredCandleV1(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadUtc(reader, 4),
                ReadUtc(reader, 5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetString(11),
                reader.GetBoolean(12),
                ReadUtc(reader, 13)));
        }

        return candles;
    }
}
