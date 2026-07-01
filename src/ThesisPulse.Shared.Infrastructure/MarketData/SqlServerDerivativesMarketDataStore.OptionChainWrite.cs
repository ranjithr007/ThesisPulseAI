using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<OptionChainIngestionResultV1> PersistOptionChainAsync(
        CanonicalOptionChainSnapshotV1 snapshot,
        CancellationToken cancellationToken = default)
    {
        var validationWarnings = DerivativesMarketDataRules.ValidateOptionChain(snapshot);
        if (!snapshot.ProviderCode.Equals(
                _options.BrokerCode,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Option-chain provider does not match the configured broker adapter.");
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
            var duplicate = await ReadOptionChainBySourceAsync(
                connection,
                transaction,
                dataSourceId,
                snapshot.SourceEventId,
                snapshot.Revision,
                cancellationToken);
            if (duplicate is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OptionChainIngestionResultV1(
                    "DUPLICATE",
                    duplicate,
                    snapshot.Entries.Count,
                    duplicate.Entries.Count,
                    snapshot.Entries.Count - duplicate.Entries.Count,
                    duplicate.Warnings);
            }

            var underlying = await ResolveInstrumentAsync(
                connection,
                transaction,
                snapshot.UnderlyingProviderInstrumentKey,
                cancellationToken);
            var warnings = new List<string>(validationWarnings);
            var accepted = new List<ResolvedOptionEntry>();
            foreach (var entry in snapshot.Entries)
            {
                var validationReason = DerivativesMarketDataRules.ValidateOptionEntry(
                    entry,
                    snapshot);
                if (validationReason is not null)
                {
                    warnings.Add($"{entry.ProviderInstrumentKey}: {validationReason}");
                    continue;
                }

                try
                {
                    var instrument = await ResolveInstrumentAsync(
                        connection,
                        transaction,
                        entry.ProviderInstrumentKey,
                        cancellationToken);
                    var contract = await ResolveContractAsync(
                        connection,
                        transaction,
                        instrument,
                        cancellationToken);
                    if (instrument.InstrumentType != "OPTION" ||
                        contract.UnderlyingInstrumentId != underlying.Id ||
                        contract.ExpiryDate != snapshot.ExpiryDate ||
                        contract.StrikePrice != entry.StrikePrice ||
                        !string.Equals(
                            contract.OptionType,
                            entry.OptionType,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add(
                            $"{entry.ProviderInstrumentKey}: option contract lineage mismatch");
                        continue;
                    }
                    accepted.Add(new ResolvedOptionEntry(contract, entry));
                }
                catch (Exception exception) when (
                    exception is KeyNotFoundException or InvalidOperationException)
                {
                    warnings.Add(
                        $"{entry.ProviderInstrumentKey}: {exception.Message}");
                }
            }

            var snapshotStatus = accepted.Count == snapshot.Entries.Count
                ? "COMPLETE"
                : accepted.Count == 0
                    ? "INVALID"
                    : "PARTIAL";
            var qualityStatus = snapshotStatus switch
            {
                "COMPLETE" => MarketDataQualityStatusV1.Valid,
                "PARTIAL" => MarketDataQualityStatusV1.Degraded,
                _ => MarketDataQualityStatusV1.Invalid,
            };
            var pointInTimeEligible = snapshotStatus == "COMPLETE";
            var expiryScheduleId = await ResolveExpiryScheduleIdAsync(
                connection,
                transaction,
                underlying.Id,
                snapshot.ExpiryDate,
                cancellationToken);
            var snapshotUid = Guid.NewGuid();
            var snapshotId = await InsertOptionChainSnapshotAsync(
                connection,
                transaction,
                dataSourceId,
                underlying.Id,
                expiryScheduleId,
                snapshotUid,
                snapshot,
                snapshotStatus,
                qualityStatus,
                pointInTimeEligible,
                accepted,
                warnings,
                cancellationToken);
            foreach (var resolved in accepted)
            {
                await InsertOptionChainEntryAsync(
                    connection,
                    transaction,
                    snapshotId,
                    resolved,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var storedEntries = accepted.Select(ToStoredEntry).ToArray();
            var stored = new StoredOptionChainSnapshotV1(
                snapshotUid,
                underlying.Uid,
                underlying.CanonicalSymbol,
                snapshot.ExpiryDate,
                snapshot.EventAtUtc,
                snapshot.PublishedAtUtc,
                snapshot.ReceivedAtUtc,
                snapshot.UnderlyingPrice,
                snapshotStatus,
                qualityStatus,
                pointInTimeEligible,
                snapshot.Revision,
                snapshot.SourceVersion,
                snapshot.CalculationSourceVersion,
                DerivativesMarketDataContractV1.OptionChainPolicyVersion,
                storedEntries,
                warnings);
            return new OptionChainIngestionResultV1(
                "CREATED",
                stored,
                snapshot.Entries.Count,
                accepted.Count,
                snapshot.Entries.Count - accepted.Count,
                warnings);
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

    private async Task<long?> ResolveExpiryScheduleIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long underlyingInstrumentId,
        DateOnly expiryDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [derivative_expiry_schedule_id]
            FROM [reference].[derivative_expiry_schedules]
            WHERE [underlying_instrument_id] = @underlying_id
              AND [market_segment] = 'OPTIONS'
              AND [expiry_date] = @expiry_date
              AND [valid_to_date] IS NULL
            ORDER BY [valid_from_date] DESC;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value =
            underlyingInstrumentId;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value = expiryDate;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> InsertOptionChainSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long dataSourceId,
        long underlyingInstrumentId,
        long? expiryScheduleId,
        Guid snapshotUid,
        CanonicalOptionChainSnapshotV1 snapshot,
        string snapshotStatus,
        string qualityStatus,
        bool pointInTimeEligible,
        IReadOnlyCollection<ResolvedOptionEntry> accepted,
        IReadOnlyCollection<string> warnings,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[option_chain_snapshots]
            ([option_chain_snapshot_uid], [data_source_id], [underlying_instrument_id],
             [derivative_expiry_schedule_id], [expiry_date], [source_event_id],
             [revision], [event_at_utc], [published_at_utc], [received_at_utc],
             [underlying_price], [snapshot_status], [quality_status],
             [is_point_in_time_eligible], [contract_count], [strike_count],
             [source_version], [calculation_source_version], [warnings_json],
             [payload_hash], [raw_payload_json], [correlation_id], [created_by])
            OUTPUT INSERTED.[option_chain_snapshot_id]
            VALUES
            (@uid, @source_id, @underlying_id, @expiry_schedule_id, @expiry_date,
             @source_event_id, @revision, @event_at, @published_at, @received_at,
             @underlying_price, @snapshot_status, @quality_status, @point_in_time,
             @contract_count, @strike_count, @source_version, @calculation_version,
             @warnings_json, @payload_hash, @raw_json, @correlation_id, @actor);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@uid", SqlDbType.UniqueIdentifier).Value = snapshotUid;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = dataSourceId;
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value =
            underlyingInstrumentId;
        command.Parameters.Add("@expiry_schedule_id", SqlDbType.BigInt).Value =
            DbValue(expiryScheduleId);
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value = snapshot.ExpiryDate;
        command.Parameters.Add("@source_event_id", SqlDbType.VarChar, 200).Value =
            snapshot.SourceEventId;
        command.Parameters.Add("@revision", SqlDbType.Int).Value = snapshot.Revision;
        command.Parameters.Add("@event_at", SqlDbType.DateTime2).Value =
            snapshot.EventAtUtc.UtcDateTime;
        command.Parameters.Add("@published_at", SqlDbType.DateTime2).Value =
            snapshot.PublishedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@received_at", SqlDbType.DateTime2).Value =
            snapshot.ReceivedAtUtc.UtcDateTime;
        command.Parameters.Add("@underlying_price", SqlDbType.Decimal).Value =
            snapshot.UnderlyingPrice;
        command.Parameters.Add("@snapshot_status", SqlDbType.VarChar, 20).Value =
            snapshotStatus;
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
            qualityStatus;
        command.Parameters.Add("@point_in_time", SqlDbType.Bit).Value = pointInTimeEligible;
        command.Parameters.Add("@contract_count", SqlDbType.Int).Value = accepted.Count;
        command.Parameters.Add("@strike_count", SqlDbType.Int).Value = accepted
            .Select(item => item.Entry.StrikePrice)
            .Distinct()
            .Count();
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 100).Value =
            snapshot.SourceVersion;
        command.Parameters.Add("@calculation_version", SqlDbType.VarChar, 100).Value =
            DbValue(snapshot.CalculationSourceVersion);
        command.Parameters.Add("@warnings_json", SqlDbType.NVarChar, -1).Value =
            SerializeWarnings(warnings);
        command.Parameters.Add("@payload_hash", SqlDbType.Char, 64).Value =
            HashPayload(snapshot.RawPayloadJson);
        command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value =
            snapshot.RawPayloadJson;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            Guid.Parse(snapshot.CorrelationId);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertOptionChainEntryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotId,
        ResolvedOptionEntry resolved,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[option_chain_entries]
            ([option_chain_snapshot_id], [derivative_contract_id], [instrument_id],
             [quote_at_utc], [strike_price], [option_type], [bid_price], [ask_price],
             [last_price], [bid_quantity], [ask_quantity], [volume_quantity],
             [open_interest], [previous_open_interest], [open_interest_change],
             [implied_volatility], [delta], [gamma], [theta], [vega], [rho],
             [greeks_source_version], [quality_status], [metadata_json], [created_by])
            VALUES
            (@snapshot_id, @contract_id, @instrument_id, @quote_at, @strike_price,
             @option_type, @bid_price, @ask_price, @last_price, @bid_quantity,
             @ask_quantity, @volume_quantity, @open_interest, @previous_oi,
             @oi_change, @iv, @delta, @gamma, @theta, @vega, @rho,
             @greeks_version, @quality_status, @metadata_json, @actor);
            """;
        var entry = resolved.Entry;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
        command.Parameters.Add("@contract_id", SqlDbType.BigInt).Value =
            resolved.Contract.Id;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            resolved.Contract.Instrument.Id;
        command.Parameters.Add("@quote_at", SqlDbType.DateTime2).Value =
            entry.QuoteAtUtc.UtcDateTime;
        command.Parameters.Add("@strike_price", SqlDbType.Decimal).Value = entry.StrikePrice;
        command.Parameters.Add("@option_type", SqlDbType.VarChar, 10).Value = entry.OptionType;
        AddNullableDecimal(command, "@bid_price", entry.BidPrice);
        AddNullableDecimal(command, "@ask_price", entry.AskPrice);
        AddNullableDecimal(command, "@last_price", entry.LastPrice);
        AddNullableDecimal(command, "@bid_quantity", entry.BidQuantity);
        AddNullableDecimal(command, "@ask_quantity", entry.AskQuantity);
        AddNullableDecimal(command, "@volume_quantity", entry.VolumeQuantity);
        AddNullableDecimal(command, "@open_interest", entry.OpenInterest);
        AddNullableDecimal(command, "@previous_oi", entry.PreviousOpenInterest);
        AddNullableDecimal(
            command,
            "@oi_change",
            entry.OpenInterest is not null && entry.PreviousOpenInterest is not null
                ? entry.OpenInterest - entry.PreviousOpenInterest
                : null);
        AddNullableDecimal(command, "@iv", entry.ImpliedVolatility);
        AddNullableDecimal(command, "@delta", entry.Delta);
        AddNullableDecimal(command, "@gamma", entry.Gamma);
        AddNullableDecimal(command, "@theta", entry.Theta);
        AddNullableDecimal(command, "@vega", entry.Vega);
        AddNullableDecimal(command, "@rho", entry.Rho);
        command.Parameters.Add("@greeks_version", SqlDbType.VarChar, 100).Value =
            DbValue(entry.GreeksSourceVersion);
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
            entry.QualityStatus;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            DbValue(SerializeMetadata(entry.Metadata));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string parameterName,
        decimal? value) =>
        command.Parameters.Add(parameterName, SqlDbType.Decimal).Value = DbValue(value);

    private static StoredOptionChainEntryV1 ToStoredEntry(ResolvedOptionEntry resolved)
    {
        var entry = resolved.Entry;
        return new StoredOptionChainEntryV1(
            resolved.Contract.Uid,
            resolved.Contract.Instrument.Uid,
            resolved.Contract.Instrument.CanonicalSymbol,
            entry.QuoteAtUtc,
            entry.StrikePrice,
            entry.OptionType,
            entry.BidPrice,
            entry.AskPrice,
            entry.LastPrice,
            entry.BidQuantity,
            entry.AskQuantity,
            entry.VolumeQuantity,
            entry.OpenInterest,
            entry.PreviousOpenInterest,
            entry.OpenInterest is not null && entry.PreviousOpenInterest is not null
                ? entry.OpenInterest - entry.PreviousOpenInterest
                : null,
            entry.ImpliedVolatility,
            entry.Delta,
            entry.Gamma,
            entry.Theta,
            entry.Vega,
            entry.Rho,
            entry.GreeksSourceVersion,
            entry.QualityStatus,
            entry.Metadata);
    }
}
