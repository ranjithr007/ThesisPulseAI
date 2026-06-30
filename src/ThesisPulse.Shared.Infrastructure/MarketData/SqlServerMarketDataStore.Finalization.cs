using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore
{
    private async Task RetireProvisionalCandlesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InstrumentMapping mapping,
        CanonicalCandleV1 finalCandle,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [market].[candles]
            SET [is_current] = 0
            WHERE [instrument_id] = @instrument_id
              AND [timeframe] = @timeframe
              AND [open_at_utc] = @open_at_utc
              AND [is_current] = 1
              AND [is_provisional] = 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            mapping.InstrumentId;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            finalCandle.Timeframe;
        command.Parameters.Add("@open_at_utc", SqlDbType.DateTime2).Value =
            finalCandle.OpenAtUtc.UtcDateTime;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
