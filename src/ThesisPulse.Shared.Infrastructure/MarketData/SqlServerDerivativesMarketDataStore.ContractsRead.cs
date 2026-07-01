using System.Data;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<IReadOnlyCollection<DerivativeContractReferenceV1>> GetContractsAsync(
        string underlyingProviderInstrumentKey,
        DateOnly? expiryDate,
        string? contractClass,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        if (!string.IsNullOrWhiteSpace(contractClass) &&
            !DerivativesMarketDataContractV1.ContractClasses.Contains(contractClass))
        {
            throw new ArgumentOutOfRangeException(nameof(contractClass));
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
                contract.[derivative_contract_uid], instrument.[instrument_uid],
                instrument.[canonical_symbol], underlying.[instrument_uid],
                underlying.[canonical_symbol], contract.[contract_class],
                contract.[expiry_date], contract.[expiry_type],
                contract.[last_trading_date], contract.[settlement_date],
                contract.[rollover_start_date], contract.[settlement_type],
                contract.[contract_multiplier], contract.[lot_size],
                contract.[strike_price], contract.[option_type], contract.[status],
                contract.[selection_eligible], contract.[valid_from_date],
                contract.[valid_to_date], contract.[metadata_json]
            FROM [reference].[derivative_contracts] contract
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = contract.[instrument_id]
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = contract.[underlying_instrument_id]
            WHERE contract.[underlying_instrument_id] = @underlying_id
              AND contract.[valid_to_date] IS NULL
              AND (@expiry_date IS NULL OR contract.[expiry_date] = @expiry_date)
              AND (@class IS NULL OR contract.[contract_class] = @class)
            ORDER BY contract.[expiry_date], contract.[strike_price], contract.[option_type];
            """;
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value = DbValue(expiryDate);
        command.Parameters.Add("@class", SqlDbType.VarChar, 30).Value =
            DbValue(contractClass?.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<DerivativeContractReferenceV1>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DerivativeContractReferenceV1(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetGuid(3), reader.GetString(4), reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)), reader.GetString(7),
                DateOnly.FromDateTime(reader.GetDateTime(8)),
                reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
                reader.IsDBNull(10) ? null : DateOnly.FromDateTime(reader.GetDateTime(10)),
                reader.GetString(11), reader.GetDecimal(12), reader.GetDecimal(13),
                ReadNullableDecimal(reader, 14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetString(16), reader.GetBoolean(17),
                DateOnly.FromDateTime(reader.GetDateTime(18)),
                reader.IsDBNull(19) ? null : DateOnly.FromDateTime(reader.GetDateTime(19)),
                ParseMetadata(reader.IsDBNull(20) ? null : reader.GetString(20))));
        }

        return results;
    }
}
