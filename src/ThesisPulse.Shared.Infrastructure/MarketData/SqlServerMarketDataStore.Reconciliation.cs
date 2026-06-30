using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore
{
    private async Task PersistLiveCandleSnapshotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IngestionBatch batch,
        long sourceId,
        InstrumentMapping mapping,
        long? sessionId,
        CanonicalLiveMarketUpdateV1 update,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!sessionId.HasValue)
        {
            return;
        }

        foreach (var snapshot in update.CandleSnapshots)
        {
            var duration = MarketDataContractV1.GetDuration(snapshot.Timeframe);
            var closeAt = snapshot.OpenAtUtc.Add(duration);
            var candle = new CanonicalCandleV1(
                update.ProviderCode,
                update.ProviderInstrumentKey,
                $"{update.SourceEventId}|{snapshot.Timeframe}|{snapshot.OpenAtUtc:O}",
                snapshot.Timeframe,
                snapshot.OpenAtUtc,
                closeAt,
                snapshot.OpenPrice,
                snapshot.HighPrice,
                snapshot.LowPrice,
                snapshot.ClosePrice,
                snapshot.VolumeQuantity,
                update.OpenInterest,
                null,
                null,
                closeAt <= update.EventAtUtc,
                update.PublishedAtUtc,
                update.ReceivedAtUtc,
                update.SourceVersion,
                update.RawPayloadJson);
            var assessment = _freshnessEvaluator.EvaluateCandle(candle, update.ReceivedAtUtc);
            if (assessment.QualityStatus == MarketDataQualityStatusV1.Invalid)
            {
                continue;
            }

            var current = await ReadCurrentRevisionAsync(
                connection,
                transaction,
                mapping.InstrumentId,
                sourceId,
                candle,
                cancellationToken);
            if (current is not null && current.Matches(candle))
            {
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
                GetTradeDate(candle.OpenAtUtc),
                candle.SourceVersion,
                candle.RawPayloadJson,
                assessment,
                correlationId,
                cancellationToken);

            if (current is not null)
            {
                await SetCurrentAsync(
                    connection,
                    transaction,
                    current.CandleId,
                    false,
                    cancellationToken);
            }

            var candleId = await InsertRevisionAsync(
                connection,
                transaction,
                observationId,
                sourceId,
                mapping,
                sessionId.Value,
                candle,
                assessment,
                current?.Revision + 1 ?? 0,
                current?.CandleId,
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
                correlationId,
                cancellationToken);
        }
    }

    private async Task<CurrentRevision?> ReadCurrentRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long instrumentId,
        long sourceId,
        CanonicalCandleV1 candle,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [candle_id], [revision], [open_price], [high_price],
                [low_price], [close_price], [volume_qty], [is_closed]
            FROM [market].[candles] WITH (UPDLOCK, HOLDLOCK)
            WHERE [instrument_id] = @instrument_id
              AND [data_source_id] = @source_id
              AND [timeframe] = @timeframe
              AND [open_at_utc] = @open_at_utc
              AND [is_current] = 1
            ORDER BY [revision] DESC;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = instrumentId;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value = candle.Timeframe;
        command.Parameters.Add("@open_at_utc", SqlDbType.DateTime2).Value = candle.OpenAtUtc.UtcDateTime;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentRevision(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetBoolean(7))
            : null;
    }

    private async Task SetCurrentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long candleId,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        const string sql = "UPDATE [market].[candles] SET [is_current] = @is_current WHERE [candle_id] = @candle_id;";
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@is_current", SqlDbType.Bit).Value = isCurrent;
        command.Parameters.Add("@candle_id", SqlDbType.BigInt).Value = candleId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> InsertRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long observationId,
        long sourceId,
        InstrumentMapping mapping,
        long sessionId,
        CanonicalCandleV1 candle,
        MarketDataFreshnessAssessmentV1 assessment,
        int revision,
        long? supersedesId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [market].[candles]
            ([source_observation_id], [instrument_id], [data_source_id],
             [trading_session_id], [trade_date], [timeframe], [open_at_utc],
             [close_at_utc], [open_price], [high_price], [low_price],
             [close_price], [volume_qty], [trade_count], [vwap_price],
             [is_closed], [is_provisional], [revision], [supersedes_candle_id],
             [is_current], [source_version], [published_at_utc], [received_at_utc],
             [processed_at_utc], [quality_status], [quality_reason_codes_json],
             [freshness_policy_version], [is_point_in_time_eligible],
             [is_usable_for_new_exposure], [created_by])
            OUTPUT INSERTED.[candle_id]
            VALUES
            (@observation_id, @instrument_id, @source_id, @session_id,
             @trade_date, @timeframe, @open_at_utc, @close_at_utc,
             @open_price, @high_price, @low_price, @close_price, @volume_qty,
             NULL, NULL, @is_closed, @is_provisional, @revision, @supersedes_id,
             1, @source_version, @published_at_utc, @received_at_utc,
             SYSUTCDATETIME(), @quality_status, @reason_codes_json,
             @policy_version, 1, @usable_new_exposure, @actor);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add("@observation_id", SqlDbType.BigInt).Value = observationId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = mapping.InstrumentId;
        command.Parameters.Add("@source_id", SqlDbType.BigInt).Value = sourceId;
        command.Parameters.Add("@session_id", SqlDbType.BigInt).Value = sessionId;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value =
            GetTradeDate(candle.OpenAtUtc).ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@timeframe", SqlDbType.VarChar, 20).Value = candle.Timeframe;
        command.Parameters.Add("@open_at_utc", SqlDbType.DateTime2).Value = candle.OpenAtUtc.UtcDateTime;
        command.Parameters.Add("@close_at_utc", SqlDbType.DateTime2).Value = candle.CloseAtUtc.UtcDateTime;
        AddDecimal(command, "@open_price", candle.OpenPrice);
        AddDecimal(command, "@high_price", candle.HighPrice);
        AddDecimal(command, "@low_price", candle.LowPrice);
        AddDecimal(command, "@close_price", candle.ClosePrice);
        AddDecimal(command, "@volume_qty", candle.VolumeQuantity);
        command.Parameters.Add("@is_closed", SqlDbType.Bit).Value = candle.IsClosed;
        command.Parameters.Add("@is_provisional", SqlDbType.Bit).Value = !candle.IsClosed;
        command.Parameters.Add("@revision", SqlDbType.Int).Value = revision;
        command.Parameters.Add("@supersedes_id", SqlDbType.BigInt).Value = (object?)supersedesId ?? DBNull.Value;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 100).Value = candle.SourceVersion;
        command.Parameters.Add("@published_at_utc", SqlDbType.DateTime2).Value = candle.PublishedAtUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value = candle.ReceivedAtUtc.UtcDateTime;
        command.Parameters.Add("@quality_status", SqlDbType.VarChar, 30).Value = assessment.QualityStatus;
        command.Parameters.Add("@reason_codes_json", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(assessment.ReasonCodes, JsonOptions);
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = assessment.PolicyVersion;
        command.Parameters.Add("@usable_new_exposure", SqlDbType.Bit).Value = assessment.IsUsableForNewExposure;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CurrentRevision(
        long CandleId, int Revision, decimal OpenPrice, decimal HighPrice,
        decimal LowPrice, decimal ClosePrice, decimal VolumeQuantity, bool IsClosed)
    {
        public bool Matches(CanonicalCandleV1 candle) =>
            OpenPrice == candle.OpenPrice && HighPrice == candle.HighPrice &&
            LowPrice == candle.LowPrice && ClosePrice == candle.ClosePrice &&
            VolumeQuantity == candle.VolumeQuantity && IsClosed == candle.IsClosed;
    }
}
