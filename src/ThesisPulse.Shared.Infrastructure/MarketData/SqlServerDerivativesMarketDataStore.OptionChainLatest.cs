using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<StoredOptionChainSnapshotV1?> GetLatestOptionChainAsync(
        string underlyingProviderInstrumentKey,
        DateOnly expiryDate,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var underlying = await ResolveInstrumentAsync(
            connection,
            transaction: null,
            underlyingProviderInstrumentKey,
            cancellationToken);
        var cutoff = (asOfUtc ?? DateTimeOffset.MaxValue).UtcDateTime;
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
            FROM [market].[option_chain_snapshots] snapshot
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = snapshot.[underlying_instrument_id]
            WHERE snapshot.[underlying_instrument_id] = @underlying_id
              AND snapshot.[expiry_date] = @expiry_date
              AND snapshot.[event_at_utc] <= @cutoff
              AND snapshot.[received_at_utc] <= @cutoff
            ORDER BY snapshot.[event_at_utc] DESC, snapshot.[revision] DESC,
                     snapshot.[received_at_utc] DESC;
            """;
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value = expiryDate;
        command.Parameters.Add("@cutoff", SqlDbType.DateTime2).Value = cutoff;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var header = ReadOptionChainHeader(reader);
        await reader.DisposeAsync();
        var entries = await ReadOptionChainEntriesAsync(
            connection,
            transaction: null,
            header.SnapshotId,
            cancellationToken);
        return header.ToContract(entries);
    }

    private static OptionChainHeader ReadOptionChainHeader(SqlDataReader reader) =>
        new(
            reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2),
            reader.GetString(3), DateOnly.FromDateTime(reader.GetDateTime(4)),
            ReadUtc(reader, 5), ReadNullableUtc(reader, 6), ReadUtc(reader, 7),
            reader.GetDecimal(8), reader.GetString(9), reader.GetString(10),
            reader.GetBoolean(11), reader.GetInt32(12), reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            ParseWarnings(reader.IsDBNull(15) ? null : reader.GetString(15)));

    private sealed record OptionChainHeader(
        long SnapshotId,
        Guid SnapshotUid,
        Guid UnderlyingInstrumentUid,
        string UnderlyingCanonicalSymbol,
        DateOnly ExpiryDate,
        DateTimeOffset EventAtUtc,
        DateTimeOffset? PublishedAtUtc,
        DateTimeOffset ReceivedAtUtc,
        decimal UnderlyingPrice,
        string SnapshotStatus,
        string QualityStatus,
        bool IsPointInTimeEligible,
        int Revision,
        string SourceVersion,
        string? CalculationSourceVersion,
        IReadOnlyCollection<string> Warnings)
    {
        public StoredOptionChainSnapshotV1 ToContract(
            IReadOnlyCollection<StoredOptionChainEntryV1> entries) =>
            new(
                SnapshotUid,
                UnderlyingInstrumentUid,
                UnderlyingCanonicalSymbol,
                ExpiryDate,
                EventAtUtc,
                PublishedAtUtc,
                ReceivedAtUtc,
                UnderlyingPrice,
                SnapshotStatus,
                QualityStatus,
                IsPointInTimeEligible,
                Revision,
                SourceVersion,
                CalculationSourceVersion,
                DerivativesMarketDataContractV1.OptionChainPolicyVersion,
                entries,
                Warnings);
    }
}
