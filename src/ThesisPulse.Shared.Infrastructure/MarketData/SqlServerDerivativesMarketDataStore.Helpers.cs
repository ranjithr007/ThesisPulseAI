using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private SqlConnection CreateConnection() => new(_options.ConnectionString);

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.Transaction = transaction;
        return command;
    }

    private async Task<long> ResolveDataSourceIdAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [data_source_id]
            FROM [market].[data_sources]
            WHERE [source_code] = @source_code AND [is_active] = 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_code", SqlDbType.VarChar, 50).Value =
            _options.HistoricalSourceCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException(
                $"Active data source '{_options.HistoricalSourceCode}' is not seeded.")
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<InstrumentRow> ResolveInstrumentAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string providerInstrumentKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                instrument.[instrument_id],
                instrument.[instrument_uid],
                instrument.[canonical_symbol],
                instrument.[instrument_type],
                instrument.[market_segment],
                instrument.[underlying_instrument_id],
                instrument.[expiry_date],
                instrument.[strike_price],
                instrument.[option_type],
                instrument.[lot_size],
                instrument.[valid_from_date],
                instrument.[valid_to_date],
                instrument.[status],
                exchange.[exchange_id],
                exchange.[exchange_code]
            FROM [reference].[broker_instrument_mappings] mapping
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = mapping.[instrument_id]
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = instrument.[exchange_id]
            WHERE broker.[broker_code] = @broker_code
              AND broker.[is_active] = 1
              AND mapping.[broker_instrument_key] = @instrument_key
              AND mapping.[is_active] = 1
              AND mapping.[valid_to_date] IS NULL
            ORDER BY instrument.[valid_from_date] DESC;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            _options.BrokerCode;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            providerInstrumentKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                $"Canonical instrument mapping '{providerInstrumentKey}' was not found.");
        }

        return ReadInstrument(reader);
    }

    private async Task<ContractRow> ResolveContractAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        InstrumentRow instrument,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                contract.[derivative_contract_id],
                contract.[derivative_contract_uid],
                contract.[underlying_instrument_id],
                contract.[contract_class],
                contract.[expiry_date],
                contract.[expiry_type],
                contract.[last_trading_date],
                contract.[settlement_date],
                contract.[rollover_start_date],
                contract.[settlement_type],
                contract.[contract_multiplier],
                contract.[lot_size],
                contract.[strike_price],
                contract.[option_type],
                contract.[status],
                contract.[selection_eligible],
                contract.[valid_from_date],
                contract.[valid_to_date],
                contract.[metadata_json]
            FROM [reference].[derivative_contracts] contract
            WHERE contract.[instrument_id] = @instrument_id
              AND contract.[valid_to_date] IS NULL
            ORDER BY contract.[valid_from_date] DESC;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = instrument.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                $"Derivative contract for '{instrument.CanonicalSymbol}' was not found.");
        }

        return new ContractRow(
            reader.GetInt64(0),
            reader.GetGuid(1),
            instrument,
            reader.GetInt64(2),
            reader.GetString(3),
            DateOnly.FromDateTime(reader.GetDateTime(4)),
            reader.GetString(5),
            DateOnly.FromDateTime(reader.GetDateTime(6)),
            reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
            reader.IsDBNull(8) ? null : DateOnly.FromDateTime(reader.GetDateTime(8)),
            reader.GetString(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetString(14),
            reader.GetBoolean(15),
            DateOnly.FromDateTime(reader.GetDateTime(16)),
            reader.IsDBNull(17) ? null : DateOnly.FromDateTime(reader.GetDateTime(17)),
            ParseMetadata(reader.IsDBNull(18) ? null : reader.GetString(18)));
    }

    private async Task<InstrumentRow> ResolveInstrumentByIdAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long instrumentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                instrument.[instrument_id],
                instrument.[instrument_uid],
                instrument.[canonical_symbol],
                instrument.[instrument_type],
                instrument.[market_segment],
                instrument.[underlying_instrument_id],
                instrument.[expiry_date],
                instrument.[strike_price],
                instrument.[option_type],
                instrument.[lot_size],
                instrument.[valid_from_date],
                instrument.[valid_to_date],
                instrument.[status],
                exchange.[exchange_id],
                exchange.[exchange_code]
            FROM [reference].[instruments] instrument
            INNER JOIN [reference].[exchanges] exchange
                ON exchange.[exchange_id] = instrument.[exchange_id]
            WHERE instrument.[instrument_id] = @instrument_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = instrumentId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                $"Canonical instrument id '{instrumentId}' was not found.");
        }

        return ReadInstrument(reader);
    }

    private static InstrumentRow ReadInstrument(SqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetDecimal(9),
            DateOnly.FromDateTime(reader.GetDateTime(10)),
            reader.IsDBNull(11) ? null : DateOnly.FromDateTime(reader.GetDateTime(11)),
            reader.GetString(12),
            reader.GetInt64(13),
            reader.GetString(14));

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal)
    {
        var value = DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
        return new DateTimeOffset(value);
    }

    private static DateTimeOffset? ReadNullableUtc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    private static decimal? ReadNullableDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static IReadOnlyDictionary<string, string>? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions);

    private static string HashPayload(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static object DbValue<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private sealed record InstrumentRow(
        long Id,
        Guid Uid,
        string CanonicalSymbol,
        string InstrumentType,
        string MarketSegment,
        long? UnderlyingInstrumentId,
        DateOnly? ExpiryDate,
        decimal? StrikePrice,
        string? OptionType,
        decimal LotSize,
        DateOnly ValidFromDate,
        DateOnly? ValidToDate,
        string Status,
        long ExchangeId,
        string ExchangeCode);

    private sealed record ContractRow(
        long Id,
        Guid Uid,
        InstrumentRow Instrument,
        long UnderlyingInstrumentId,
        string ContractClass,
        DateOnly ExpiryDate,
        string ExpiryType,
        DateOnly LastTradingDate,
        DateOnly? SettlementDate,
        DateOnly? RolloverStartDate,
        string SettlementType,
        decimal ContractMultiplier,
        decimal LotSize,
        decimal? StrikePrice,
        string? OptionType,
        string Status,
        bool SelectionEligible,
        DateOnly ValidFromDate,
        DateOnly? ValidToDate,
        IReadOnlyDictionary<string, string>? Metadata);

    private sealed record ResolvedOptionEntry(
        ContractRow Contract,
        CanonicalOptionChainEntryV1 Entry);
}
