using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed class AutomaticTradePlanTargetOptions
{
    public int Sequence { get; init; }
    public decimal RiskRewardMultiple { get; init; }
    public decimal QuantityFraction { get; init; }
}

public sealed class AutomaticTradePlanIntakeOptions
{
    public const string SectionName = "AutomaticTradePlanIntake";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public string SessionCode { get; init; } = "REGULAR";
    public int EntryCutoffBufferMinutes { get; init; } = 15;
    public string PositionIntent { get; init; } = "INTRADAY";
    public string EntryOrderType { get; init; } = "MARKET";
    public string StopOrderType { get; init; } = "STOP_MARKET";
    public decimal MaximumSlippageFraction { get; init; } = 0.001m;
    public string TimeInForce { get; init; } = "DAY";
    public bool AllowPartialFill { get; init; } = true;
    public int MinimumExecutionLots { get; init; } = 1;
    public bool AllowTrailingStop { get; init; } = true;
    public bool AllowBreakEvenMove { get; init; } = true;
    public bool AllowTimeExit { get; init; } = true;
    public bool AllowSignalExit { get; init; } = true;
    public string ExitPolicyVersion { get; init; } = "exit-policy-v1.0.0";
    public string ExecutionPolicyVersion { get; init; } = "execution-policy-v1.0.0";
    public List<AutomaticTradePlanTargetOptions> Targets { get; init; } =
    [
        new() { Sequence = 1, RiskRewardMultiple = 2m, QuantityFraction = 0.5m },
        new() { Sequence = 2, RiskRewardMultiple = 3m, QuantityFraction = 0.5m },
    ];

    public void Validate()
    {
        if (!Enabled)
            return;
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticTradePlanIntake:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("AutomaticTradePlanIntake:BatchSize must be between 1 and 500.");
        if (EntryCutoffBufferMinutes is < 0 or > 180)
            throw new InvalidOperationException("AutomaticTradePlanIntake:EntryCutoffBufferMinutes must be between 0 and 180.");
        if (MinimumExecutionLots is < 1 or > 100000)
            throw new InvalidOperationException("AutomaticTradePlanIntake:MinimumExecutionLots must be positive.");
        if (MaximumSlippageFraction is < 0 or > 0.1m)
            throw new InvalidOperationException("AutomaticTradePlanIntake:MaximumSlippageFraction is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(PositionIntent);
        ArgumentException.ThrowIfNullOrWhiteSpace(EntryOrderType);
        ArgumentException.ThrowIfNullOrWhiteSpace(StopOrderType);
        ArgumentException.ThrowIfNullOrWhiteSpace(TimeInForce);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExitPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutionPolicyVersion);
        if (Targets.Count == 0 || Targets.Any(target =>
                target.Sequence < 1 || target.RiskRewardMultiple <= 0 || target.QuantityFraction <= 0))
            throw new InvalidOperationException("AutomaticTradePlanIntake:Targets are invalid.");
        if (Targets.Select(target => target.Sequence).Distinct().Count() != Targets.Count ||
            Targets.OrderBy(target => target.Sequence).Select((target, index) => target.Sequence == index + 1).Any(valid => !valid))
            throw new InvalidOperationException("AutomaticTradePlanIntake:Target sequences must be contiguous from one.");
        if (Targets.Sum(target => target.QuantityFraction) != 1m)
            throw new InvalidOperationException("AutomaticTradePlanIntake:Target fractions must total exactly one.");
    }
}

public sealed record AutomaticTradePlanCandidate(
    long SignalRiskEvaluationId,
    Guid EvaluationCommandUid,
    Guid EvaluationSourceMessageUid,
    string CorrelationId,
    RiskDecisionV1 RiskDecision,
    SignalGeneratedV1 Signal,
    decimal LotSize,
    bool IsTradeAllowed,
    bool IsShortAllowed,
    string TimeZoneId,
    DateOnly TradeDate,
    bool IsTradingDay,
    TimeOnly SessionStartLocal,
    TimeOnly SessionEndLocal,
    bool SessionCrossesMidnight,
    bool IsOrderEntryAllowed);

public interface IAutomaticTradePlanCandidateStore
{
    Task<IReadOnlyCollection<AutomaticTradePlanCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        string sessionCode,
        CancellationToken cancellationToken);
}

public sealed class SqlServerAutomaticTradePlanCandidateStore(
    SignalRiskPersistenceOptions persistenceOptions) : IAutomaticTradePlanCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<AutomaticTradePlanCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        string sessionCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                e.[signal_risk_evaluation_id], e.[command_uid], e.[source_message_uid],
                e.[correlation_id], e.[decision_snapshot_json], s.[raw_contract_json],
                i.[lot_size], i.[is_trade_allowed], i.[is_short_allowed], x.[timezone_id],
                c.[exchange_calendar_id], ts.[start_time_local], ts.[end_time_local],
                ts.[crosses_midnight], ts.[is_order_entry_allowed]
            FROM [risk].[signal_risk_evaluations] e WITH (READPAST)
            INNER JOIN [intelligence].[signals] s ON s.[signal_id] = e.[signal_id]
            INNER JOIN [reference].[instruments] i ON i.[instrument_id] = s.[instrument_id]
            INNER JOIN [reference].[exchanges] x ON x.[exchange_id] = i.[exchange_id]
            INNER JOIN [reference].[exchange_calendars] c
                ON c.[exchange_id] = x.[exchange_id]
               AND c.[status] = 'ACTIVE'
            OUTER APPLY
            (
                SELECT TOP (1)
                    session.[start_time_local], session.[end_time_local],
                    session.[crosses_midnight], session.[is_order_entry_allowed]
                FROM [reference].[trading_sessions] session
                WHERE session.[exchange_calendar_id] = c.[exchange_calendar_id]
                  AND session.[session_code] = @session_code
                  AND session.[market_segment] IN (i.[market_segment], 'ALL')
                ORDER BY
                    CASE WHEN session.[market_segment] = i.[market_segment] THEN 0 ELSE 1 END,
                    session.[valid_from_date] DESC
            ) ts
            WHERE e.[current_status] = 'RISK_APPROVED'
              AND e.[decision_snapshot_json] IS NOT NULL
              AND s.[valid_until_utc] > @as_of_utc
              AND i.[status] = 'ACTIVE'
              AND i.[valid_from_date] <= CAST(@as_of_utc AS date)
              AND (i.[valid_to_date] IS NULL OR i.[valid_to_date] >= CAST(@as_of_utc AS date))
              AND ts.[start_time_local] IS NOT NULL
              AND NOT EXISTS
              (
                  SELECT 1 FROM [risk].[trade_plan_work_items] w
                  WHERE w.[signal_risk_evaluation_id] = e.[signal_risk_evaluation_id]
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM [risk].[trade_plans] p
                  WHERE p.[signal_risk_evaluation_id] = e.[signal_risk_evaluation_id]
                    AND p.[is_current] = 1
              )
            ORDER BY e.[updated_at_utc], e.[signal_risk_evaluation_id];
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;
        command.Parameters.Add("@session_code", SqlDbType.VarChar, 30).Value = sessionCode;

        var rows = new List<RawCandidate>(maximumCount);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var decision = JsonSerializer.Deserialize<RiskDecisionV1>(reader.GetString(4), JsonOptions)
                    ?? throw new InvalidOperationException("Approved Risk decision snapshot could not be deserialized.");
                var signal = JsonSerializer.Deserialize<SignalGeneratedV1>(reader.GetString(5), JsonOptions)
                    ?? throw new InvalidOperationException("Canonical Signal snapshot could not be deserialized.");
                rows.Add(new RawCandidate(
                    reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3).ToString("D"),
                    decision, signal, reader.GetDecimal(6), reader.GetBoolean(7), reader.GetBoolean(8),
                    reader.GetString(9), reader.GetInt64(10), reader.GetTimeSpan(11), reader.GetTimeSpan(12),
                    reader.GetBoolean(13), reader.GetBoolean(14)));
            }
        }

        var candidates = new List<AutomaticTradePlanCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (!TryResolveTradeDate(asOfUtc, row.TimeZoneId, out var tradeDate))
                continue;
            var tradingDay = await IsTradingDayAsync(
                connection,
                row.ExchangeCalendarId,
                tradeDate,
                cancellationToken);
            candidates.Add(new AutomaticTradePlanCandidate(
                row.SignalRiskEvaluationId,
                row.EvaluationCommandUid,
                row.EvaluationSourceMessageUid,
                row.CorrelationId,
                row.RiskDecision,
                row.Signal,
                row.LotSize,
                row.IsTradeAllowed,
                row.IsShortAllowed,
                row.TimeZoneId,
                tradeDate,
                tradingDay,
                TimeOnly.FromTimeSpan(row.SessionStart),
                TimeOnly.FromTimeSpan(row.SessionEnd),
                row.SessionCrossesMidnight,
                row.IsOrderEntryAllowed));
        }
        return candidates;
    }

    private static async Task<bool> IsTradingDayAsync(
        SqlConnection connection,
        long calendarId,
        DateOnly tradeDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [is_trading_day]
            FROM [reference].[calendar_days]
            WHERE [exchange_calendar_id] = @calendar_id
              AND [trade_date] = @trade_date;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@calendar_id", SqlDbType.BigInt).Value = calendarId;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value = tradeDate.ToDateTime(TimeOnly.MinValue);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool isTradingDay && isTradingDay;
    }

    private static bool TryResolveTradeDate(
        DateTimeOffset asOfUtc,
        string timeZoneId,
        out DateOnly tradeDate)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            tradeDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOfUtc, zone).DateTime);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tradeDate = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            tradeDate = default;
            return false;
        }
    }

    private sealed record RawCandidate(
        long SignalRiskEvaluationId,
        Guid EvaluationCommandUid,
        Guid EvaluationSourceMessageUid,
        string CorrelationId,
        RiskDecisionV1 RiskDecision,
        SignalGeneratedV1 Signal,
        decimal LotSize,
        bool IsTradeAllowed,
        bool IsShortAllowed,
        string TimeZoneId,
        long ExchangeCalendarId,
        TimeSpan SessionStart,
        TimeSpan SessionEnd,
        bool SessionCrossesMidnight,
        bool IsOrderEntryAllowed);
}

public static class AutomaticTradePlanContextAssembler
{
    public static bool TryBuild(
        AutomaticTradePlanCandidate candidate,
        AutomaticTradePlanIntakeOptions options,
        DateTimeOffset asOfUtc,
        out AutomaticTradePlanIntakeV1? intake,
        out string reason)
    {
        intake = null;
        if (!candidate.IsTradingDay)
        {
            reason = "EXCHANGE_NOT_TRADING";
            return false;
        }
        if (!candidate.IsTradeAllowed || !candidate.IsOrderEntryAllowed)
        {
            reason = "ORDER_ENTRY_NOT_ALLOWED";
            return false;
        }
        if (candidate.RiskDecision.Direction == Thesis.V1.EvidenceDirectionV1.Short && !candidate.IsShortAllowed)
        {
            reason = "SHORT_NOT_ALLOWED";
            return false;
        }
        if (!TrySession(candidate, out var sessionStartUtc, out var sessionEndUtc))
        {
            reason = "SESSION_TIME_INVALID";
            return false;
        }

        var entryCutoff = Min(
            candidate.Signal.EntryClosesAtUtc,
            sessionEndUtc.AddMinutes(-options.EntryCutoffBufferMinutes));
        if (asOfUtc < sessionStartUtc || asOfUtc >= entryCutoff || entryCutoff >= sessionEndUtc)
        {
            reason = "ENTRY_WINDOW_CLOSED";
            return false;
        }

        var messageUid = DeterministicGuidV1.Create(
            candidate.EvaluationCommandUid,
            "automatic-trade-plan-intake-v1");
        intake = new AutomaticTradePlanIntakeV1(
            messageUid,
            candidate.CorrelationId,
            candidate.EvaluationSourceMessageUid,
            candidate.RiskDecision,
            candidate.Signal,
            new TradePlanInstrumentContextV1(
                candidate.LotSize,
                null,
                candidate.LotSize * options.MinimumExecutionLots,
                options.AllowPartialFill),
            new TradePlanExecutionContextV1(
                options.PositionIntent,
                options.EntryOrderType,
                options.StopOrderType,
                null,
                options.MaximumSlippageFraction,
                options.TimeInForce,
                options.Targets
                    .OrderBy(target => target.Sequence)
                    .Select(target => new TradePlanTargetPolicyV1(
                        target.Sequence,
                        target.RiskRewardMultiple,
                        target.QuantityFraction))
                    .ToArray(),
                new TradeSessionV1(
                    candidate.TradeDate,
                    sessionStartUtc,
                    entryCutoff,
                    sessionEndUtc),
                new ExitPolicyV1(
                    options.AllowTrailingStop,
                    options.AllowBreakEvenMove,
                    options.AllowTimeExit,
                    options.AllowSignalExit,
                    options.ExitPolicyVersion),
                options.ExecutionPolicyVersion),
            asOfUtc);
        reason = string.Empty;
        return true;
    }

    private static bool TrySession(
        AutomaticTradePlanCandidate candidate,
        out DateTimeOffset startUtc,
        out DateTimeOffset endUtc)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(candidate.TimeZoneId);
            var startLocal = DateTime.SpecifyKind(
                candidate.TradeDate.ToDateTime(candidate.SessionStartLocal),
                DateTimeKind.Unspecified);
            var endDate = candidate.SessionCrossesMidnight
                ? candidate.TradeDate.AddDays(1)
                : candidate.TradeDate;
            var endLocal = DateTime.SpecifyKind(
                endDate.ToDateTime(candidate.SessionEndLocal),
                DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal))
            {
                startUtc = default;
                endUtc = default;
                return false;
            }
            startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
            endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);
            return endUtc > startUtc;
        }
        catch (TimeZoneNotFoundException)
        {
            startUtc = default;
            endUtc = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            startUtc = default;
            endUtc = default;
            return false;
        }
    }

    private static DateTimeOffset Min(params DateTimeOffset[] values) => values.Min();
}

public sealed class AutomaticTradePlanIntakeWorker(
    AutomaticTradePlanIntakeOptions options,
    IAutomaticTradePlanCandidateStore candidateStore,
    IAutomaticTradePlanWorkQueue queue,
    ILogger<AutomaticTradePlanIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var candidates = await candidateStore.ReadPendingAsync(
                    options.BatchSize,
                    now,
                    options.SessionCode,
                    stoppingToken);
                foreach (var candidate in candidates)
                {
                    if (!AutomaticTradePlanContextAssembler.TryBuild(candidate, options, now, out var intake, out var reason))
                    {
                        logger.LogWarning(
                            "Automatic Trade Plan discovery skipped Risk evaluation {EvaluationId}: {Reason}.",
                            candidate.SignalRiskEvaluationId,
                            reason);
                        continue;
                    }
                    await queue.EnqueueAsync(intake!, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic approved-Risk-to-Trade-Plan discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
