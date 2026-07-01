using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<FuturesBasisIngestionResultV1> PersistFuturesBasisAsync(
        CanonicalFuturesBasisObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        DerivativesMarketDataRules.ValidateFuturesBasis(observation);
        if (!observation.ProviderCode.Equals(
                _options.BrokerCode,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Futures-basis provider does not match the configured broker adapter.");
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var dataSourceId = await ResolveDataSourceIdAsync(
                connection,
                transaction,
                cancellationToken);
            var duplicate = await ReadBasisBySourceAsync(
                connection,
                transaction,
                dataSourceId,
                observation.SourceEventId,
                observation.Revision,
                cancellationToken);
            if (duplicate is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new FuturesBasisIngestionResultV1(
                    "DUPLICATE",
                    duplicate,
                    Array.Empty<string>());
            }

            var underlying = await ResolveInstrumentAsync(
                connection,
                transaction,
                observation.UnderlyingProviderInstrumentKey,
                cancellationToken);
            var future = await ResolveInstrumentAsync(
                connection,
                transaction,
                observation.FutureProviderInstrumentKey,
                cancellationToken);
            if (future.InstrumentType != "FUTURE" ||
                future.UnderlyingInstrumentId != underlying.Id)
            {
                throw new InvalidOperationException(
                    "Futures-basis instrument lineage does not match the canonical catalog.");
            }

            var contract = await ResolveContractAsync(
                connection,
                transaction,
                future,
                cancellationToken);
            var calculated = DerivativesMarketDataRules.CalculateBasis(
                observation,
                contract.ExpiryDate);
            var observationUid = Guid.NewGuid();
            const string insertSql = """
                INSERT INTO [market].[futures_basis_observations]
                ([futures_basis_observation_uid], [data_source_id],
                 [underlying_instrument_id], [future_instrument_id],
                 [derivative_contract_id], [source_event_id], [revision],
                 [event_at_utc], [published_at_utc], [received_at_utc],
                 [underlying_price], [future_price], [basis_amount], [basis_fraction],
                 [days_to_expiry], [annualized_basis_fraction], [quality_status],
                 [is_point_in_time_eligible], [source_version], [payload_hash],
                 [raw_payload_json], [correlation_id], [created_by])
                VALUES
                (@uid, @source_id, @underlying_id, @future_id, @contract_id,
                 @source_event_id, @revision, @event_at, @published_at, @received_at,
                 @underlying_price, @future_price, @basis_amount, @basis_fraction,
                 @days_to_expiry, @annualized_basis, @quality_status,
                 @point_in_time, @source_version, @payload_hash, @raw_json,
                 @correlation_id, @actor);
                """;
            await using var command = CreateCommand(connection, transaction, insertSql);
            command.Parameters.Add("@uid", SqlDbType.UniqueIdentifier).Value = observationUid;
            command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = dataSourceId;
            command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
            command.Parameters.Add("@future_id", SqlDbType.BigInt).Value = future.Id;
            command.Parameters.Add("@contract_id", SqlDbType.BigInt).Value = contract.Id;
            command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value =
                observation.SourceEventId;
            command.Parameters.Add("@revision", SqlDbType.Int).Value = observation.Revision;
            command.Parameters.Add("@event_at", SqlDbType.DateTime2).Value =
                observation.EventAtUtc.UtcDateTime;
            command.Parameters.Add("@published_at", SqlDbType.DateTime2).Value =
                observation.PublishedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
            command.Parameters.Add("@received_at", SqlDbType.DateTime2).Value =
                observation.ReceivedAtUtc.UtcDateTime;
            command.Parameters.Add("@underlying_price", SqlDbType.Decimal).Value =
                observation.UnderlyingPrice;
            command.Parameters.Add("@future_price", SqlDbType.Decimal).Value =
                observation.FuturePrice;
            command.Parameters.Add("@basis_amount", SqlDbType.Decimal).Value =
                calculated.BasisAmount;
            command.Parameters.Add("@basis_fraction", SqlDbType.Decimal).Value =
                calculated.BasisFraction;
            command.Parameters.Add("@days_to_expiry", SqlDbType.Int).Value =
                calculated.DaysToExpiry;
            command.Parameters.Add("@annualized_basis", SqlDbType.Decimal).Value =
                DbValue(calculated.AnnualizedBasisFraction);
            command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
                calculated.QualityStatus;
            command.Parameters.Add("@point_in_time", SqlDbType.Bit).Value =
                calculated.IsPointInTimeEligible;
            command.Parameters.Add("@source_version", SqlDbType.VarChar, 100).Value =
                observation.SourceVersion;
            command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
                HashPayload(observation.RawPayloadJson);
            command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value =
                observation.RawPayloadJson;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
                Guid.Parse(observation.CorrelationId);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var stored = new StoredFuturesBasisObservationV1(
                observationUid,
                underlying.Uid,
                underlying.CanonicalSymbol,
                future.Uid,
                future.CanonicalSymbol,
                contract.Uid,
                contract.ExpiryDate,
                observation.EventAtUtc,
                observation.PublishedAtUtc,
                observation.ReceivedAtUtc,
                observation.UnderlyingPrice,
                observation.FuturePrice,
                calculated.BasisAmount,
                calculated.BasisFraction,
                calculated.DaysToExpiry,
                calculated.AnnualizedBasisFraction,
                calculated.QualityStatus,
                calculated.IsPointInTimeEligible,
                observation.Revision,
                observation.SourceVersion,
                DerivativesMarketDataContractV1.BasisPolicyVersion);
            return new FuturesBasisIngestionResultV1(
                "CREATED",
                stored,
                calculated.Warnings);
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }

    public async Task<StoredFuturesBasisObservationV1?> GetLatestFuturesBasisAsync(
        string futureProviderInstrumentKey,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(futureProviderInstrumentKey);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var future = await ResolveInstrumentAsync(
            connection,
            transaction: null,
            futureProviderInstrumentKey,
            cancellationToken);
        var cutoff = (asOfUtc ?? DateTimeOffset.MaxValue).UtcDateTime;
        const string sql = """
            SELECT TOP (1)
                basis.[futures_basis_observation_uid],
                underlying.[instrument_uid], underlying.[canonical_symbol],
                future.[instrument_uid], future.[canonical_symbol],
                contract.[derivative_contract_uid], contract.[expiry_date],
                basis.[event_at_utc], basis.[published_at_utc], basis.[received_at_utc],
                basis.[underlying_price], basis.[future_price], basis.[basis_amount],
                basis.[basis_fraction], basis.[days_to_expiry],
                basis.[annualized_basis_fraction], basis.[quality_status],
                basis.[is_point_in_time_eligible], basis.[revision], basis.[source_version]
            FROM [market].[futures_basis_observations] basis
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = basis.[underlying_instrument_id]
            INNER JOIN [reference].[instruments] future
                ON future.[instrument_id] = basis.[future_instrument_id]
            INNER JOIN [reference].[derivative_contracts] contract
                ON contract.[derivative_contract_id] = basis.[derivative_contract_id]
            WHERE basis.[future_instrument_id] = @future_id
              AND basis.[event_at_utc] <= @cutoff
              AND basis.[received_at_utc] <= @cutoff
            ORDER BY basis.[event_at_utc] DESC, basis.[revision] DESC,
                     basis.[received_at_utc] DESC;
            """;
        await using var command = CreateCommand(connection, transaction: null, sql);
        command.Parameters.Add("@future_id", SqlDbType.BigInt).Value = future.Id;
        command.Parameters.Add("@cutoff", SqlDbType.DateTime2).Value = cutoff;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBasis(reader) : null;
    }

    private async Task<StoredFuturesBasisObservationV1?> ReadBasisBySourceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long dataSourceId,
        string sourceEventId,
        int revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                basis.[futures_basis_observation_uid],
                underlying.[instrument_uid], underlying.[canonical_symbol],
                future.[instrument_uid], future.[canonical_symbol],
                contract.[derivative_contract_uid], contract.[expiry_date],
                basis.[event_at_utc], basis.[published_at_utc], basis.[received_at_utc],
                basis.[underlying_price], basis.[future_price], basis.[basis_amount],
                basis.[basis_fraction], basis.[days_to_expiry],
                basis.[annualized_basis_fraction], basis.[quality_status],
                basis.[is_point_in_time_eligible], basis.[revision], basis.[source_version]
            FROM [market].[futures_basis_observations] basis WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[instruments] underlying
                ON underlying.[instrument_id] = basis.[underlying_instrument_id]
            INNER JOIN [reference].[instruments] future
                ON future.[instrument_id] = basis.[future_instrument_id]
            INNER JOIN [reference].[derivative_contracts] contract
                ON contract.[derivative_contract_id] = basis.[derivative_contract_id]
            WHERE basis.[data_source_id] = @source_id
              AND basis.[source_event_id] = @source_event_id
              AND basis.[revision] = @revision;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = dataSourceId;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value = sourceEventId;
        command.Parameters.Add("@revision", SqlDbType.Int).Value = revision;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBasis(reader) : null;
    }

    private static StoredFuturesBasisObservationV1 ReadBasis(SqlDataReader reader) =>
        new(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetGuid(3), reader.GetString(4), reader.GetGuid(5),
            DateOnly.FromDateTime(reader.GetDateTime(6)), ReadUtc(reader, 7),
            ReadNullableUtc(reader, 8), ReadUtc(reader, 9), reader.GetDecimal(10),
            reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13),
            reader.GetInt32(14), ReadNullableDecimal(reader, 15), reader.GetString(16),
            reader.GetBoolean(17), reader.GetInt32(18), reader.GetString(19),
            DerivativesMarketDataContractV1.BasisPolicyVersion);
}
