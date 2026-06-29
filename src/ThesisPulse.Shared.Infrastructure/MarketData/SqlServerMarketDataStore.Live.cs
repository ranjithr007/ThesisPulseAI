using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed partial class SqlServerMarketDataStore
{
    public async Task<LiveMarketIngestionResultV1> PersistLiveUpdatesAsync(
        IReadOnlyCollection<CanonicalLiveMarketUpdateV1> updates,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

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
                _options.LiveSourceCode,
                cancellationToken);
            var batch = await StartBatchAsync(
                connection,
                transaction,
                sourceId,
                null,
                "QUOTE",
                null,
                "LIVE",
                correlationId,
                null,
                null,
                cancellationToken);

            foreach (var update in updates.OrderBy(item => item.EventAtUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var assessment = _freshnessEvaluator.EvaluateLiveUpdate(
                        update,
                        update.ReceivedAtUtc);

                    if (assessment.QualityStatus == MarketDataQualityStatusV1.Invalid)
                    {
                        rejected++;
                        warnings.Add(
                            $"Rejected live event '{update.SourceEventId}': " +
                            string.Join(",", assessment.ReasonCodes));
                        continue;
                    }

                    if (await ObservationExistsAsync(
                            connection,
                            transaction,
                            sourceId,
                            update.SourceEventId,
                            cancellationToken))
                    {
                        duplicates++;
                        continue;
                    }

                    var mapping = await ResolveInstrumentMappingAsync(
                        connection,
                        transaction,
                        update.ProviderInstrumentKey,
                        cancellationToken);
                    var tradeDate = GetTradeDate(update.EventAtUtc);
                    var sessionId = await ResolveTradingSessionIdAsync(
                        connection,
                        transaction,
                        mapping,
                        tradeDate,
                        cancellationToken);
                    var observationId = await InsertObservationAsync(
                        connection,
                        transaction,
                        batch,
                        sourceId,
                        mapping,
                        sessionId,
                        "QUOTE",
                        null,
                        update.SourceEventId,
                        update.EventAtUtc,
                        update.PublishedAtUtc,
                        update.ReceivedAtUtc,
                        tradeDate,
                        update.SourceVersion,
                        update.RawPayloadJson,
                        assessment,
                        correlationId,
                        cancellationToken);
                    await InsertQualityAssessmentAsync(
                        connection,
                        transaction,
                        sourceId,
                        mapping,
                        observationId,
                        null,
                        "QUOTE",
                        null,
                        assessment,
                        correlationId,
                        cancellationToken);
                    await UpsertCursorAsync(
                        connection,
                        transaction,
                        sourceId,
                        mapping,
                        "QUOTE",
                        null,
                        update.SourceEventId,
                        update.EventAtUtc,
                        update.ReceivedAtUtc,
                        batch.BatchId,
                        assessment.QualityStatus == MarketDataQualityStatusV1.Stale
                            ? "STALE"
                            : "HEALTHY",
                        cancellationToken);

                    accepted++;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or KeyNotFoundException)
                {
                    rejected++;
                    warnings.Add(
                        $"Rejected live event '{update.SourceEventId}': " +
                        exception.Message);
                }
            }

            var status = DetermineBatchStatus(accepted, duplicates, rejected);
            await CompleteBatchAsync(
                connection,
                transaction,
                batch.BatchId,
                updates.Count,
                accepted,
                duplicates,
                rejected,
                status,
                rejected > 0 ? string.Join(" | ", warnings.Take(20)) : null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new LiveMarketIngestionResultV1(
                batch.BatchUid,
                updates.FirstOrDefault()?.ProviderCode ?? _options.BrokerCode,
                updates.Count,
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
}
