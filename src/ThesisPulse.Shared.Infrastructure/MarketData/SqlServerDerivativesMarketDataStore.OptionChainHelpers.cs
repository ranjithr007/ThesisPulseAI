using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    private async Task<StoredOptionChainSnapshotV1?> ReadOptionChainBySourceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long dataSourceId,
        string sourceEventId,
        int revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                snapshot.[option_chain_snapshot_id],
                snapshot.[option_chain_snapshot_uid],
                underlying.[instrument_uid], underlying.[canonical_symbol],
                snapshot.[expiry_date], snapshot.[event_at_utc],
                snapshot.[published_at_utc], snapshot.[received_at_utc],
                snapshot.[underlying_price], snapshot.[snapshot_status],
                snapshot.[quality_status], snapshot.[is_point_in_time_eligible],
                snapshot.[revision], snapshot.[source_version],
                snapshot.[calculation_source_version], snapshot.[warnings_json]
            FROM [market].[option_chain_snapshots] snapshot WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = snapshot.[underlying_instrument_id]
            WHERE snapshot.[data_source_id] = @source_id
              AND snapshot.[source_event_id] = @source_event_id
              AND snapshot.[revision] = @revision;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = dataSourceId;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value = sourceEventId;
        command.Parameters.Add("@revision", SqlDbType.Int).Value = revision;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var header = ReadOptionChainHeader(reader);
        await reader.DisposeAsync();
        var entries = await ReadOptionChainEntriesAsync(
            connection,
            transaction,
            header.SnapshotId,
            cancellationToken);
        return header.ToContract(entries);
    }

    private async Task<IReadOnlyCollection<StoredOptionChainEntryV1>>
        ReadOptionChainEntriesAsync(
            SqlConnection connection,
            SqlTransaction? transaction,
            long snapshotId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                contract.[derivative_contract_uid], instrument.[instrument_uid],
                instrument.[canonical_symbol], entry.[quote_at_utc],
                entry.[strike_price], entry.[option_type], entry.[bid_price],
                entry.[ask_price], entry.[last_price], entry.[bid_quantity],
                entry.[ask_quantity], entry.[volume_quantity], entry.[open_interest],
                entry.[previous_open_interest], entry.[open_interest_change],
                entry.[implied_volatility], entry.[delta], entry.[gamma], entry.[theta],
                entry.[vega], entry.[rho], entry.[greeks_source_version],
                entry.[quality_status], entry.[metadata_json]
            FROM [market].[option_chain_entries] entry
            INNER JOIN [reference].[derivative_contracts] contract
                ON contract.[derivative_contract_id] = entry.[derivative_contract_id]
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = entry.[instrument_id]
            WHERE entry.[option_chain_snapshot_id] = @snapshot_id
            ORDER BY entry.[strike_price], entry.[option_type];
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<StoredOptionChainEntryV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new StoredOptionChainEntryV1(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                ReadUtc(reader, 3), reader.GetDecimal(4), reader.GetString(5),
                ReadNullableDecimal(reader, 6), ReadNullableDecimal(reader, 7),
                ReadNullableDecimal(reader, 8), ReadNullableDecimal(reader, 9),
                ReadNullableDecimal(reader, 10), ReadNullableDecimal(reader, 11),
                ReadNullableDecimal(reader, 12), ReadNullableDecimal(reader, 13),
                ReadNullableDecimal(reader, 14), ReadNullableDecimal(reader, 15),
                ReadNullableDecimal(reader, 16), ReadNullableDecimal(reader, 17),
                ReadNullableDecimal(reader, 18), ReadNullableDecimal(reader, 19),
                ReadNullableDecimal(reader, 20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.GetString(22),
                ParseMetadata(reader.IsDBNull(23) ? null : reader.GetString(23))));
        }
        return entries;
    }
}
