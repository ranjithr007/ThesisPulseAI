using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class SqlServerInstrumentCatalogStore : IInstrumentCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerMarketDataOptions _options;

    public SqlServerInstrumentCatalogStore(SqlServerMarketDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<InstrumentSynchronizationResultV1> SynchronizeAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var created = 0;
        var updated = 0;
        var mappingCreated = 0;
        var mappingUpdated = 0;
        var skipped = 0;
        var warnings = new List<string>();

        try
        {
            var brokerId = await ResolveBrokerIdAsync(
                connection,
                transaction,
                cancellationToken);

            var ordered = instruments
                .OrderBy(instrument => IsDerivative(instrument) ? 1 : 0)
                .ThenBy(instrument => instrument.ProviderInstrumentKey)
                .ToArray();

            foreach (var instrument in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var exchangeId = await ResolveExchangeIdAsync(
                        connection,
                        transaction,
                        instrument.ExchangeCode,
                        cancellationToken);
                    var underlyingId = await ResolveUnderlyingIdAsync(
                        connection,
                        transaction,
                        brokerId,
                        instrument,
                        cancellationToken);

                    if (IsDerivative(instrument) && underlyingId is null)
                    {
                        skipped++;
                        warnings.Add(
                            $"Skipped '{instrument.ProviderInstrumentKey}' because its " +
                            "underlying instrument mapping was not available.");
                        continue;
                    }

                    var instrumentResult = await UpsertInstrumentAsync(
                        connection,
                        transaction,
                        exchangeId,
                        underlyingId,
                        instrument,
                        cancellationToken);

                    if (instrumentResult.Created)
                    {
                        created++;
                    }
                    else
                    {
                        updated++;
                    }

                    var mappingResult = await UpsertBrokerMappingAsync(
                        connection,
                        transaction,
                        brokerId,
                        instrumentResult.InstrumentId,
                        instrument,
                        cancellationToken);

                    if (mappingResult.Created)
                    {
                        mappingCreated++;
                    }
                    else
                    {
                        mappingUpdated++;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or KeyNotFoundException)
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

        return new InstrumentSynchronizationResultV1(
            instruments.FirstOrDefault()?.ProviderCode ?? _options.BrokerCode,
            snapshotReceivedAtUtc,
            instruments.Count,
            created,
            updated,
            mappingCreated,
            mappingUpdated,
            skipped,
            warnings.Take(200).ToArray());
    }

    private async Task<long> ResolveBrokerIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [broker_id]
            FROM [reference].[brokers] WITH (UPDLOCK, HOLDLOCK)
            WHERE [broker_code] = @broker_code
              AND [is_active] = 1;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            _options.BrokerCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? throw new InvalidOperationException(
                $"Active broker '{_options.BrokerCode}' is not seeded.")
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> ResolveExchangeIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string exchangeCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [exchange_id]
            FROM [reference].[exchanges] WITH (UPDLOCK, HOLDLOCK)
            WHERE [exchange_code] = @exchange_code
              AND [is_active] = 1;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@exchange_code", SqlDbType.VarChar, 20).Value =
            exchangeCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? throw new KeyNotFoundException(
                $"Active exchange '{exchangeCode}' is not available.")
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long?> ResolveUnderlyingIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long brokerId,
        CanonicalInstrumentV1 instrument,
        CancellationToken cancellationToken)
    {
        if (!IsDerivative(instrument))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(instrument.UnderlyingProviderInstrumentKey))
        {
            return null;
        }

        const string sql = """
            SELECT mapping.[instrument_id]
            FROM [reference].[broker_instrument_mappings] mapping
                WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [reference].[instruments] instrument
                ON instrument.[instrument_id] = mapping.[instrument_id]
            WHERE mapping.[broker_id] = @broker_id
              AND mapping.[broker_instrument_key] = @instrument_key
              AND mapping.[is_active] = 1
              AND mapping.[valid_to_date] IS NULL
              AND instrument.[status] = 'ACTIVE';
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@broker_id", SqlDbType.BigInt).Value = brokerId;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            instrument.UnderlyingProviderInstrumentKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<UpsertResult> UpsertInstrumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long exchangeId,
        long? underlyingInstrumentId,
        CanonicalInstrumentV1 instrument,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT TOP (1) [instrument_id]
            FROM [reference].[instruments] WITH (UPDLOCK, HOLDLOCK)
            WHERE [exchange_id] = @exchange_id
              AND [canonical_symbol] = @canonical_symbol
              AND [valid_to_date] IS NULL
            ORDER BY [valid_from_date] DESC;
            """;

        await using var selectCommand = CreateCommand(connection, transaction, selectSql);
        selectCommand.Parameters.Add("@exchange_id", SqlDbType.BigInt).Value = exchangeId;
        selectCommand.Parameters.Add("@canonical_symbol", SqlDbType.VarChar, 100).Value =
            instrument.CanonicalSymbol;
        var existingValue = await selectCommand.ExecuteScalarAsync(cancellationToken);

        if (existingValue is not null and not DBNull)
        {
            var instrumentId = Convert.ToInt64(
                existingValue,
                System.Globalization.CultureInfo.InvariantCulture);
            await UpdateInstrumentAsync(
                connection,
                transaction,
                instrumentId,
                underlyingInstrumentId,
                instrument,
                cancellationToken);
            return new UpsertResult(instrumentId, Created: false);
        }

        const string insertSql = """
            INSERT INTO [reference].[instruments]
            (
                [exchange_id], [canonical_symbol], [display_name],
                [instrument_type], [market_segment], [base_currency_code],
                [tick_size], [lot_size], [price_scale], [quantity_scale],
                [underlying_instrument_id], [expiry_date], [strike_price],
                [option_type], [status], [valid_from_date], [valid_to_date],
                [is_trade_allowed], [is_short_allowed],
                [created_by], [updated_by]
            )
            OUTPUT INSERTED.[instrument_id]
            VALUES
            (
                @exchange_id, @canonical_symbol, @display_name,
                @instrument_type, @market_segment, 'INR',
                @tick_size, @lot_size, @price_scale, @quantity_scale,
                @underlying_instrument_id, @expiry_date, @strike_price,
                @option_type, 'ACTIVE', @valid_from_date, NULL,
                @is_trade_allowed, @is_short_allowed,
                @actor, @actor
            );
            """;

        await using var insertCommand = CreateCommand(connection, transaction, insertSql);
        AddInstrumentParameters(
            insertCommand,
            exchangeId,
            underlyingInstrumentId,
            instrument);
        var inserted = await insertCommand.ExecuteScalarAsync(cancellationToken);
        var insertedId = Convert.ToInt64(
            inserted,
            System.Globalization.CultureInfo.InvariantCulture);
        return new UpsertResult(insertedId, Created: true);
    }

    private async Task UpdateInstrumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long instrumentId,
        long? underlyingInstrumentId,
        CanonicalInstrumentV1 instrument,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [reference].[instruments]
            SET [display_name] = @display_name,
                [instrument_type] = @instrument_type,
                [market_segment] = @market_segment,
                [tick_size] = @tick_size,
                [lot_size] = @lot_size,
                [price_scale] = @price_scale,
                [quantity_scale] = @quantity_scale,
                [underlying_instrument_id] = @underlying_instrument_id,
                [expiry_date] = @expiry_date,
                [strike_price] = @strike_price,
                [option_type] = @option_type,
                [status] = 'ACTIVE',
                [is_trade_allowed] = @is_trade_allowed,
                [is_short_allowed] = @is_short_allowed,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [instrument_id] = @instrument_id;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        AddInstrumentParameters(
            command,
            exchangeId: null,
            underlyingInstrumentId,
            instrument);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = instrumentId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<UpsertResult> UpsertBrokerMappingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long brokerId,
        long instrumentId,
        CanonicalInstrumentV1 instrument,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT TOP (1)
                [broker_instrument_mapping_id], [instrument_id]
            FROM [reference].[broker_instrument_mappings] WITH (UPDLOCK, HOLDLOCK)
            WHERE [broker_id] = @broker_id
              AND [broker_instrument_key] = @instrument_key
              AND [is_active] = 1
              AND [valid_to_date] IS NULL;
            """;

        await using var selectCommand = CreateCommand(connection, transaction, selectSql);
        selectCommand.Parameters.Add("@broker_id", SqlDbType.BigInt).Value = brokerId;
        selectCommand.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            instrument.ProviderInstrumentKey;
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);

        long? mappingId = null;
        long? mappedInstrumentId = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            mappingId = reader.GetInt64(0);
            mappedInstrumentId = reader.GetInt64(1);
        }
        await reader.DisposeAsync();

        var metadataJson = JsonSerializer.Serialize(
            new
            {
                instrument.Isin,
                instrument.FreezeQuantity,
                source = instrument.Metadata,
            },
            JsonOptions);

        if (mappingId.HasValue)
        {
            if (mappedInstrumentId != instrumentId)
            {
                throw new InvalidOperationException(
                    "Provider instrument key is already mapped to another instrument.");
            }

            const string updateSql = """
                UPDATE [reference].[broker_instrument_mappings]
                SET [broker_symbol] = @broker_symbol,
                    [broker_exchange_code] = @exchange_code,
                    [broker_segment] = @segment,
                    [metadata_json] = @metadata_json,
                    [updated_at_utc] = SYSUTCDATETIME(),
                    [updated_by] = @actor
                WHERE [broker_instrument_mapping_id] = @mapping_id;
                """;

            await using var updateCommand = CreateCommand(
                connection,
                transaction,
                updateSql);
            AddMappingParameters(updateCommand, instrument, metadataJson);
            updateCommand.Parameters.Add("@mapping_id", SqlDbType.BigInt).Value =
                mappingId.Value;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return new UpsertResult(mappingId.Value, Created: false);
        }

        const string insertSql = """
            INSERT INTO [reference].[broker_instrument_mappings]
            (
                [broker_id], [instrument_id], [broker_instrument_key],
                [broker_symbol], [broker_exchange_code], [broker_segment],
                [valid_from_date], [valid_to_date], [is_active], [metadata_json],
                [created_by], [updated_by]
            )
            OUTPUT INSERTED.[broker_instrument_mapping_id]
            VALUES
            (
                @broker_id, @instrument_id, @instrument_key,
                @broker_symbol, @exchange_code, @segment,
                @valid_from_date, NULL, 1, @metadata_json,
                @actor, @actor
            );
            """;

        await using var insertCommand = CreateCommand(connection, transaction, insertSql);
        insertCommand.Parameters.Add("@broker_id", SqlDbType.BigInt).Value = brokerId;
        insertCommand.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = instrumentId;
        AddMappingParameters(insertCommand, instrument, metadataJson);
        var value = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return new UpsertResult(
            Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            Created: true);
    }

    private void AddInstrumentParameters(
        SqlCommand command,
        long? exchangeId,
        long? underlyingInstrumentId,
        CanonicalInstrumentV1 instrument)
    {
        if (exchangeId.HasValue)
        {
            command.Parameters.Add("@exchange_id", SqlDbType.BigInt).Value =
                exchangeId.Value;
        }

        command.Parameters.Add("@canonical_symbol", SqlDbType.VarChar, 100).Value =
            instrument.CanonicalSymbol;
        command.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value =
            instrument.DisplayName;
        command.Parameters.Add("@instrument_type", SqlDbType.VarChar, 30).Value =
            instrument.InstrumentType;
        command.Parameters.Add("@market_segment", SqlDbType.VarChar, 30).Value =
            instrument.MarketSegment;
        AddDecimal(command, "@tick_size", instrument.TickSize, 19, 6);
        AddDecimal(command, "@lot_size", instrument.LotSize, 19, 6);
        command.Parameters.Add("@price_scale", SqlDbType.SmallInt).Value =
            CalculateScale(instrument.TickSize);
        command.Parameters.Add("@quantity_scale", SqlDbType.SmallInt).Value =
            CalculateScale(instrument.LotSize);
        command.Parameters.Add("@underlying_instrument_id", SqlDbType.BigInt).Value =
            (object?)underlyingInstrumentId ?? DBNull.Value;
        command.Parameters.Add("@expiry_date", SqlDbType.Date).Value =
            instrument.ExpiryDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value;
        AddNullableDecimal(command, "@strike_price", instrument.StrikePrice, 19, 6);
        command.Parameters.Add("@option_type", SqlDbType.VarChar, 10).Value =
            (object?)instrument.OptionType ?? DBNull.Value;
        command.Parameters.Add("@valid_from_date", SqlDbType.Date).Value =
            instrument.EffectiveFromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@is_trade_allowed", SqlDbType.Bit).Value =
            instrument.IsTradeAllowed;
        command.Parameters.Add("@is_short_allowed", SqlDbType.Bit).Value =
            instrument.IsShortAllowed;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
    }

    private void AddMappingParameters(
        SqlCommand command,
        CanonicalInstrumentV1 instrument,
        string metadataJson)
    {
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            instrument.ProviderInstrumentKey;
        command.Parameters.Add("@broker_symbol", SqlDbType.VarChar, 100).Value =
            instrument.CanonicalSymbol;
        command.Parameters.Add("@exchange_code", SqlDbType.VarChar, 50).Value =
            instrument.ExchangeCode;
        command.Parameters.Add("@segment", SqlDbType.VarChar, 50).Value =
            instrument.ProviderSegment;
        command.Parameters.Add("@valid_from_date", SqlDbType.Date).Value =
            instrument.EffectiveFromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            metadataJson;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static bool IsDerivative(CanonicalInstrumentV1 instrument) =>
        instrument.InstrumentType is "FUTURE" or "OPTION";

    private static short CalculateScale(decimal value)
    {
        value = decimal.Abs(value);
        var bits = decimal.GetBits(value);
        return (short)((bits[3] >> 16) & 0x7F);
    }

    private static void AddDecimal(
        SqlCommand command,
        string name,
        decimal value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string name,
        decimal? value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private sealed record UpsertResult(long InstrumentId, bool Created);
}
