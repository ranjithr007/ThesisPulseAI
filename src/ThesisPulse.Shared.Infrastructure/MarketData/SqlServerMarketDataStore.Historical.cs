using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore
{
    public async Task<HistoricalCandleIngestionResultV1> PersistHistoricalCandlesAsync(
        HistoricalCandleRequestV1 request,
        IReadOnlyCollection<CanonicalCandleV1> candles,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoricalRequest(request);
        ArgumentNullException.ThrowIfNull(candles);

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        var warnings = new List<string>();

        try
        {
            var sourceId = await ResolveSourceIdAsync(
                connection,
                transaction,
                _options.HistoricalSourceCode,
                cancellationToken);
            var mapping = await ResolveInstrumentMappingAsync(
                connection,
                transaction,
                request.ProviderInstrumentKey,
                cancellationToken);
            var requestedFromUtc = ToIndiaDateBoundaryUtc(request.FromDate, endOfDay: false);
            var requestedToUtc = ToIndiaDateBoundaryUtc(request.ToDate, endOfDay: true);
            var batch = await StartBatchAsync(
                connection,
                transaction,
                sourceId,
                mapping.InstrumentId,
                "CANDLE",
                request.Timeframe,
                "HISTORICAL",
                request.CorrelationId,
                requestedFromUtc,
                requestedToUtc,
                cancellationToken);

            CanonicalCandleV1? latestAccepted = null;

            foreach (var candle in candles.OrderBy(item => item.OpenAtUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!CandleMatchesRequest(candle, request))
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected candle '{candle.SourceEventId}' because its scope " +
                        "does not match the request.");
                    continue;
                }

                var assessment = _freshnessEvaluator.EvaluateCandle(
                    candle,
                    candle.ReceivedAtUtc);

                if (assessment.QualityStatus == MarketDataQualityStatusV1.Invalid)
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected invalid candle '{candle.SourceEventId}': " +
                        string.Join(",", assessment.ReasonCodes));
                    continue;
                }

                if (await ObservationExistsAsync(
                        connection,
                        transaction,
                        sourceId,
                        candle.SourceEventId,
                        cancellationToken))
                {
                    duplicates++;
                    continue;
                }

                var tradeDate = GetTradeDate(candle.OpenAtUtc);
                var sessionId = await ResolveTradingSessionIdAsync(
                    connection,
                    transaction,
                    mapping,
                    tradeDate,
                    cancellationToken);

                if (!sessionId.HasValue)
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected candle '{candle.SourceEventId}' because no " +
                        $"trading session was found for {tradeDate:yyyy-MM-dd}.");
                    continue;
                }

                var observationId = await InsertObservationAsync(
                    connection,
                    transaction,
                    batch,
                    sourceId,
                    mapping,
                    sessionId,
                    "CANDLE",
                    candle.Timeframe,
                    candle.SourceEventId,
                    candle.OpenAtUtc,
                    candle.PublishedAtUtc,
                    candle.ReceivedAtUtc,
                    tradeDate,
                    candle.SourceVersion,
                    candle.RawPayloadJson,
                    assessment,
                    request.CorrelationId,
                    cancellationToken);
                var candleId = await InsertCandleAsync(
                    connection,
                    transaction,
                    observationId,
                    sourceId,
                    mapping,
                    sessionId.Value,
                    tradeDate,
                    candle,
                    assessment,
                    cancellationToken);
                await InsertQualityAssessmentAsync(
                    connection,
                    transaction,
                    sourceId,
                    mapping,
                    observationId,
                    candleId,
                    "CANDLE",
                    candle.Timeframe,
                    assessment,
                    request.CorrelationId,
                    cancellationToken);

                accepted++;
                latestAccepted = candle;
            }

            var status = DetermineBatchStatus(accepted, duplicates, rejected);
            await CompleteBatchAsync(
                connection,
                transaction,
                batch.BatchId,
                candles.Count,
                accepted,
                duplicates,
                rejected,
                status,
                rejected > 0 ? string.Join(" | ", warnings.Take(20)) : null,
                cancellationToken);

            if (latestAccepted is not null)
            {
                var latestAssessment = _freshnessEvaluator.EvaluateCandle(
                    latestAccepted,
                    latestAccepted.ReceivedAtUtc);
                await UpsertCursorAsync(
                    connection,
                    transaction,
                    sourceId,
                    mapping,
                    "CANDLE",
                    request.Timeframe,
                    latestAccepted.SourceEventId,
                    latestAccepted.OpenAtUtc,
                    latestAccepted.ReceivedAtUtc,
                    batch.BatchId,
                    latestAssessment.QualityStatus == MarketDataQualityStatusV1.Stale
                        ? "STALE"
                        : "HEALTHY",
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new HistoricalCandleIngestionResultV1(
                batch.BatchUid,
                candles.FirstOrDefault()?.ProviderCode ?? _options.BrokerCode,
                request.ProviderInstrumentKey,
                request.Timeframe,
                request.FromDate,
                request.ToDate,
                candles.Count,
                accepted,
                duplicates,
                rejected,
                status,
                warnings.Take(200).ToArray());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<long> InsertCandleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long observationId,
        long sourceId,
        InstrumentMapping mapping,
        long tradingSessionId,
        DateOnly tradeDate,
        CanonicalCandleV1 candle,
        MarketDataFreshnessAssessmentV1 assessment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[candles]
            (
                [source_observation_id], [instrument_id], [data_source_id],
                [trading_session_id], [trade_date], [timeframe],
                [open_at_utc], [close_at_utc],
                [open_price], [high_price], [low_price], [close_price],
                [volume_qty], [trade_count], [vwap_price],
                [is_closed], [is_provisional], [revision],
                [supersedes_candle_id], [is_current], [source_version],
                [published_at_utc], [received_at_utc], [processed_at_utc],
                [quality_status], [quality_reason_codes_json],
                [freshness_policy_version], [is_point_in_time_eligible],
                [is_usable_for_new_exposure], [created_by]
            )
            OUTPUT INSERTED.[candle_id]
            VALUES
            (
                @observation_id, @instrument_id, @source_id,
                @session_id, @trade_date, @timeframe,
                @open_at_utc, @close_at_utc,
                @open_price, @high_price, @low_price, @close_price,
                @volume_qty, @trade_count, @vwap_price,
                1, 0, 0,
                NULL, 1, @source_version,
                @published_at_utc, @received_at_utc, SYSUTCDATETIME(),
                @quality_status, @reason_codes_json,
                @policy_version, @point_in_time_eligible,
                @usable_new_exposure, @actor
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@observation_id", SqlDbType.BigInt).Value = observationId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value =
            mapping.InstrumentId;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@session_id", SqlDbType.BigInt).Value = tradingSessionId;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value =
            tradeDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value =
            candle.Timeframe;
        command.Parameters.Add("@open_at_utc", SqlDbType.DateTime2).Value =
            candle.OpenAtUtc.UtcDateTime;
        command.Parameters.Add("@close_at_utc", SqlDbType.DateTime2).Value =
            candle.CloseAtUtc.UtcDateTime;
        AddDecimal(command, "@open_price", candle.OpenPrice);
        AddDecimal(command, "@high_price", candle.HighPrice);
        AddDecimal(command, "@low_price", candle.LowPrice);
        AddDecimal(command, "@close_price", candle.ClosePrice);
        AddDecimal(command, "@volume_qty", candle.VolumeQuantity);
        command.Parameters.Add("@trade_count", SqlDbType.BigInt).Value =
            (object?)candle.TradeCount ?? DBNull.Value;
        AddNullableDecimal(command, "@vwap_price", candle.VwapPrice);
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 100).Value =
            candle.SourceVersion;
        command.Parameters.Add("@published_at_utc", SqlDbType.DateTime2).Value =
            candle.PublishedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value =
            candle.ReceivedAtUtc.UtcDateTime;
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value =
            assessment.QualityStatus;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(
                assessment.ReasonCodes,
                JsonOptions);
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value =
            assessment.PolicyVersion;
        command.Parameters.Add("@point_in_time_eligible", SqlDbType.Bit).Value =
            assessment.QualityStatus != MarketDataQualityStatusV1.Invalid;
        command.Parameters.Add("@usable_new_exposure", SqlDbType.Bit).Value =
            assessment.IsUsableForNewExposure;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool CandleMatchesRequest(
        CanonicalCandleV1 candle,
        HistoricalCandleRequestV1 request) =>
        candle.ProviderInstrumentKey.Equals(
            request.ProviderInstrumentKey,
            StringComparison.OrdinalIgnoreCase) &&
        candle.Timeframe.Equals(request.Timeframe, StringComparison.OrdinalIgnoreCase) &&
        candle.IsClosed;

    private static void ValidateHistoricalRequest(HistoricalCandleRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderInstrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);

        if (!MarketDataContractV1.Timeframes.Contains(request.Timeframe))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Timeframe));
        }

        if (request.ToDate < request.FromDate)
        {
            throw new ArgumentException("ToDate must be on or after FromDate.");
        }

        if (request.ToDate.DayNumber - request.FromDate.DayNumber > 3650)
        {
            throw new ArgumentException("Historical request exceeds the ten-year limit.");
        }
    }

    private static DateTimeOffset ToIndiaDateBoundaryUtc(
        DateOnly date,
        bool endOfDay)
    {
        var time = endOfDay ? new TimeOnly(23, 59, 59, 999) : TimeOnly.MinValue;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = IndiaTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static string DetermineBatchStatus(
        int accepted,
        int duplicates,
        int rejected) =>
        rejected == 0
            ? "SUCCEEDED"
            : accepted + duplicates > 0
                ? "PARTIAL"
                : "FAILED";

    private static void AddDecimal(
        SqlCommand command,
        string name,
        decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 6;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string name,
        decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 6;
        parameter.Value = (object?)value ?? DBNull.Value;
    }
}
