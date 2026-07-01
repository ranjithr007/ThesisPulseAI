using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerDerivativesMarketDataStore
{
    public async Task<DerivativeContractSynchronizationResultV1> SynchronizeContractsAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        var created = 0;
        var updated = 0;
        var expiryCreated = 0;
        var expiryUpdated = 0;
        var skipped = 0;
        var warnings = new List<string>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            foreach (var instrument in instruments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = DerivativesMarketDataRules.DescribeContract(instrument);
                if (descriptor is null)
                {
                    if (instrument.InstrumentType is "FUTURE" or "OPTION")
                    {
                        skipped++;
                        warnings.Add(
                            $"Skipped '{instrument.ProviderInstrumentKey}' because canonical " +
                            "derivative metadata was incomplete.");
                    }
                    continue;
                }

                try
                {
                    var derivative = await ResolveInstrumentAsync(
                        connection,
                        transaction,
                        instrument.ProviderInstrumentKey,
                        cancellationToken);
                    var underlying = await ResolveInstrumentAsync(
                        connection,
                        transaction,
                        instrument.UnderlyingProviderInstrumentKey!,
                        cancellationToken);
                    if (derivative.UnderlyingInstrumentId != underlying.Id)
                    {
                        throw new InvalidOperationException(
                            "Canonical derivative-to-underlying relationship does not match.");
                    }

                    if (await UpsertContractAsync(
                            connection,
                            transaction,
                            derivative,
                            underlying,
                            descriptor,
                            cancellationToken))
                    {
                        created++;
                    }
                    else
                    {
                        updated++;
                    }

                    if (await UpsertExpiryAsync(
                            connection,
                            transaction,
                            derivative,
                            underlying,
                            descriptor,
                            snapshotReceivedAtUtc,
                            cancellationToken))
                    {
                        expiryCreated++;
                    }
                    else
                    {
                        expiryUpdated++;
                    }
                }
                catch (Exception exception) when (
                    exception is KeyNotFoundException or InvalidOperationException)
                {
                    skipped++;
                    warnings.Add(
                        $"Skipped '{instrument.ProviderInstrumentKey}': {exception.Message}");
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new DerivativeContractSynchronizationResultV1(
            instruments.FirstOrDefault()?.ProviderCode ?? _options.BrokerCode,
            snapshotReceivedAtUtc,
            instruments.Count(item => item.InstrumentType is "FUTURE" or "OPTION"),
            created,
            updated,
            expiryCreated,
            expiryUpdated,
            skipped,
            warnings.Take(500).ToArray());
    }

    private async Task<bool> UpsertContractAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InstrumentRow derivative,
        InstrumentRow underlying,
        DerivativeContractDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT TOP (1) [derivative_contract_id]
            FROM [reference].[derivative_contracts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [instrument_id] = @instrument_id AND [valid_to_date] IS NULL;
            """;
        await using var selectCommand = CreateCommand(connection, transaction, selectSql);
        selectCommand.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = derivative.Id;
        var existing = await selectCommand.ExecuteScalarAsync(cancellationToken);
        var metadataJson = SerializeMetadata(descriptor.Instrument.Metadata);

        if (existing is not null and not DBNull)
        {
            const string updateSql = """
                UPDATE [reference].[derivative_contracts]
                SET [underlying_instrument_id] = @underlying_id,
                    [contract_class] = @contract_class,
                    [expiry_date] = @expiry_date,
                    [expiry_type] = @expiry_type,
                    [last_trading_date] = @last_trading_date,
                    [settlement_date] = @settlement_date,
                    [rollover_start_date] = @rollover_start_date,
                    [settlement_type] = @settlement_type,
                    [contract_multiplier] = @contract_multiplier,
                    [lot_size] = @lot_size,
                    [strike_price] = @strike_price,
                    [option_type] = @option_type,
                    [status] = @status,
                    [selection_eligible] = 0,
                    [metadata_json] = @metadata_json,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
                WHERE [derivative_contract_id] = @contract_id;
                """;
            await using var updateCommand = CreateCommand(connection, transaction, updateSql);
            AddContractParameters(updateCommand, derivative, underlying, descriptor, metadataJson);
            updateCommand.Parameters.Add("@contract_id", SqlDbType.BigInt).Value =
                Convert.ToInt64(existing, System.Globalization.CultureInfo.InvariantCulture);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return false;
        }

        const string insertSql = """
            INSERT INTO [reference].[derivative_contracts]
            ([derivative_contract_uid], [instrument_id], [underlying_instrument_id],
             [contract_class], [expiry_date], [expiry_type], [last_trading_date],
             [settlement_date], [rollover_start_date], [settlement_type],
             [contract_multiplier], [lot_size], [strike_price], [option_type],
             [status], [selection_eligible], [valid_from_date], [valid_to_date],
             [metadata_json], [created_by], [updated_by])
            VALUES
            (NEWID(), @instrument_id, @underlying_id, @contract_class, @expiry_date,
             @expiry_type, @last_trading_date, @settlement_date, @rollover_start_date,
             @settlement_type, @contract_multiplier, @lot_size, @strike_price,
             @option_type, @status, 0, @valid_from_date, NULL, @metadata_json,
             @actor, @actor);
            """;
        await using var insertCommand = CreateCommand(connection, transaction, insertSql);
        AddContractParameters(insertCommand, derivative, underlying, descriptor, metadataJson);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private async Task<bool> UpsertExpiryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        InstrumentRow derivative,
        InstrumentRow underlying,
        DerivativeContractDescriptor descriptor,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken)
    {
        var segment = derivative.InstrumentType == "FUTURE" ? "FUTURES" : "OPTIONS";
        const string selectSql = """
            SELECT TOP (1) [derivative_expiry_schedule_id]
            FROM [reference].[derivative_expiry_schedules] WITH (UPDLOCK, HOLDLOCK)
            WHERE [underlying_instrument_id] = @underlying_id
              AND [market_segment] = @market_segment
              AND [expiry_date] = @expiry_date
              AND [valid_to_date] IS NULL;
            """;
        await using var selectCommand = CreateCommand(connection, transaction, selectSql);
        selectCommand.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        selectCommand.Parameters.Add("@market_segment", SqlDbType.VarChar, 20).Value = segment;
        selectCommand.Parameters.Add("@expiry_date", SqlDbType.Date).Value =
            descriptor.Instrument.ExpiryDate!.Value;
        var existing = await selectCommand.ExecuteScalarAsync(cancellationToken);
        var status = descriptor.Instrument.ExpiryDate.Value >= DateOnly.FromDateTime(
            snapshotReceivedAtUtc.UtcDateTime)
            ? "SCHEDULED"
            : "EXPIRED";

        if (existing is not null and not DBNull)
        {
            const string updateSql = """
                UPDATE [reference].[derivative_expiry_schedules]
                SET [expiry_type] = @expiry_type,
                    [last_trading_date] = @last_trading_date,
                    [settlement_date] = @settlement_date,
                    [rollover_start_date] = @rollover_start_date,
                    [status] = @status,
                    [calendar_version] = @calendar_version,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
                WHERE [derivative_expiry_schedule_id] = @schedule_id;
                """;
            await using var updateCommand = CreateCommand(connection, transaction, updateSql);
            AddExpiryParameters(
                updateCommand,
                derivative,
                underlying,
                descriptor,
                segment,
                status);
            updateCommand.Parameters.Add("@schedule_id", SqlDbType.BigInt).Value =
                Convert.ToInt64(existing, System.Globalization.CultureInfo.InvariantCulture);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return false;
        }

        const string insertSql = """
            INSERT INTO [reference].[derivative_expiry_schedules]
            ([derivative_expiry_schedule_uid], [exchange_id],
             [underlying_instrument_id], [market_segment], [expiry_date],
             [expiry_type], [last_trading_date], [settlement_date],
             [rollover_start_date], [status], [calendar_version],
             [valid_from_date], [valid_to_date], [metadata_json],
             [created_by], [updated_by])
            VALUES
            (NEWID(), @exchange_id, @underlying_id, @market_segment, @expiry_date,
             @expiry_type, @last_trading_date, @settlement_date,
             @rollover_start_date, @status, @calendar_version,
             @valid_from_date, NULL, NULL, @actor, @actor);
            """;
        await using var insertCommand = CreateCommand(connection, transaction, insertSql);
        AddExpiryParameters(
            insertCommand,
            derivative,
            underlying,
            descriptor,
            segment,
            status);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private void AddContractParameters(
        SqlCommand command,
        InstrumentRow derivative,
        InstrumentRow underlying,
        DerivativeContractDescriptor descriptor,
        string? metadataJson)
    {
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = derivative.Id;
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        command.Parameters.Add("@contract_class", SqlDbType.VarChar, 30).Value =
            descriptor.ContractClass;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value =
            descriptor.Instrument.ExpiryDate!.Value;
        command.Parameters.Add("@expiry_type", SqlDbType.VarChar, 20).Value = descriptor.ExpiryType;
        command.Parameters.Add("@last_trading_date", SqlDbType.Date).Value =
            descriptor.LastTradingDate;
        command.Parameters.Add("@settlement_date", SqlDbType.Date).Value =
            DbValue(descriptor.SettlementDate);
        command.Parameters.Add("@rollover_start_date", SqlDbType.Date).Value =
            DbValue(descriptor.RolloverStartDate);
        command.Parameters.Add("@settlement_type", SqlDbType.VarChar, 20).Value =
            descriptor.SettlementType;
        command.Parameters.Add("@contract_multiplier", SqlDbType.Decimal).Value =
            descriptor.ContractMultiplier;
        command.Parameters.Add("@lot_size", SqlDbType.Decimal).Value =
            descriptor.Instrument.LotSize;
        command.Parameters.Add("@strike_price", SqlDbType.Decimal).Value =
            DbValue(descriptor.Instrument.StrikePrice);
        command.Parameters.Add("@option_type", SqlDbType.VarChar, 10).Value =
            DbValue(descriptor.Instrument.OptionType);
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
            descriptor.Instrument.ExpiryDate.Value < DateOnly.FromDateTime(DateTime.UtcNow)
                ? "EXPIRED"
                : "ACTIVE";
        command.Parameters.Add("@valid_from_date", SqlDbType.Date).Value =
            descriptor.Instrument.EffectiveFromDate;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            DbValue(metadataJson);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }

    private void AddExpiryParameters(
        SqlCommand command,
        InstrumentRow derivative,
        InstrumentRow underlying,
        DerivativeContractDescriptor descriptor,
        string segment,
        string status)
    {
        command.Parameters.Add("@exchange_id", SqlDbType.BigInt).Value = derivative.ExchangeId;
        command.Parameters.Add("@underlying_id", SqlDbType.BigInt).Value = underlying.Id;
        command.Parameters.Add("@market_segment", SqlDbType.VarChar, 20).Value = segment;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value =
            descriptor.Instrument.ExpiryDate!.Value;
        command.Parameters.Add("@expiry_type", SqlDbType.VarChar, 20).Value = descriptor.ExpiryType;
        command.Parameters.Add("@last_trading_date", SqlDbType.Date).Value =
            descriptor.LastTradingDate;
        command.Parameters.Add("@settlement_date", SqlDbType.Date).Value =
            DbValue(descriptor.SettlementDate);
        command.Parameters.Add("@rollover_start_date", SqlDbType.Date).Value =
            DbValue(descriptor.RolloverStartDate);
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
        command.Parameters.Add("@calendar_version", SqlDbType.VarChar, 100).Value =
            DerivativesMarketDataContractV1.CatalogPolicyVersion;
        command.Parameters.Add("@valid_from_date", SqlDbType.Date).Value =
            descriptor.Instrument.EffectiveFromDate;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
    }
}
