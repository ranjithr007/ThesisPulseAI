using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public sealed class PaperTradeLifecycleReadOptions
{
    public const string SectionName = "PaperTradeLifecycleRead";

    public int DefaultLimit { get; set; } = 50;
    public int MaximumLimit { get; set; } = 200;
    public int MaximumAgeMinutes { get; set; } = 15;

    public void Validate()
    {
        if (DefaultLimit is < 1 or > 200)
            throw new InvalidOperationException("Paper lifecycle default limit must be between 1 and 200.");
        if (MaximumLimit is < 1 or > 500 || MaximumLimit < DefaultLimit)
            throw new InvalidOperationException("Paper lifecycle maximum limit must be between the default limit and 500.");
        if (MaximumAgeMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Paper lifecycle maximum age must be between 1 and 1440 minutes.");
    }
}

public interface IPaperTradeLifecycleReadStore
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    Task<IReadOnlyCollection<PaperTradeLifecycleSummaryV1>> ReadRecentAsync(
        string? portfolioCode,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<PaperTradeLifecycleDetailV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken);
}

public sealed class UnavailablePaperTradeLifecycleReadStore : IPaperTradeLifecycleReadStore
{
    public bool IsAvailable => false;
    public string? UnavailableReason =>
        "PAPER lifecycle reads require SQL Server execution persistence.";

    public Task<IReadOnlyCollection<PaperTradeLifecycleSummaryV1>> ReadRecentAsync(
        string? portfolioCode,
        int maximumCount,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<PaperTradeLifecycleSummaryV1>>([]);

    public Task<PaperTradeLifecycleDetailV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken) =>
        Task.FromResult<PaperTradeLifecycleDetailV1?>(null);
}

public sealed class SqlServerPaperTradeLifecycleReadStore(
    SqlServerPaperExecutionLedgerOptions ledgerOptions,
    PaperTradeLifecycleReadOptions readOptions) : IPaperTradeLifecycleReadStore
{
    private const string LifecycleSql = """
        SELECT TOP (@maximum_count)
            tp.[correlation_id],
            portfolio_route.[portfolio_code],
            CONCAT(ex.[exchange_code], '|', i.[canonical_symbol]) AS [instrument_key],
            s.[strategy_code],
            s.[direction],
            s.[signal_uid],
            s.[initial_status] AS [signal_status],
            s.[generated_at_utc] AS [signal_at_utc],
            COALESCE(t.[thesis_uid], candidate_lineage.[thesis_uid]) AS [thesis_uid],
            COALESCE(
                t.[initial_status],
                CASE WHEN candidate_lineage.[thesis_uid] IS NOT NULL THEN 'CANDIDATE' END
            ) AS [thesis_status],
            COALESCE(t.[generated_at_utc], candidate_lineage.[created_at_utc]) AS [thesis_at_utc],
            COALESCE(sre.[risk_decision_uid], rd.[risk_decision_uid]) AS [risk_decision_uid],
            COALESCE(sre.[current_status], rd.[decision]) AS [risk_status],
            COALESCE(sre.[updated_at_utc], rd.[evaluated_at_utc]) AS [risk_at_utc],
            tp.[trade_plan_uid],
            tp.[initial_status] AS [trade_plan_status],
            tp.[generated_at_utc] AS [trade_plan_at_utc],
            command_state.[execution_command_uid],
            command_state.[command_status],
            command_state.[outcome_classification],
            command_state.[command_at_utc],
            command_state.[last_error_code],
            command_state.[last_error_message],
            o.[order_uid],
            o.[current_status] AS [order_status],
            o.[requested_quantity],
            o.[filled_quantity],
            o.[average_fill_price],
            o.[updated_at_utc] AS [order_at_utc],
            fill_summary.[fill_count],
            fill_summary.[total_fill_quantity],
            fill_summary.[weighted_average_fill_price],
            fill_summary.[last_fill_at_utc],
            position_link.[position_uid],
            position_link.[position_status],
            position_link.[position_quantity],
            position_link.[average_open_price],
            position_link.[realized_pnl_amount],
            position_link.[unrealized_pnl_amount],
            position_link.[position_at_utc],
            pnl_link.[pnl_snapshot_uid],
            pnl_link.[position_net_pnl_amount],
            pnl_link.[pnl_as_of_utc]
        FROM [risk].[trade_plans] tp WITH (READPAST)
        INNER JOIN [intelligence].[signals] s
            ON s.[signal_id] = tp.[signal_id]
        INNER JOIN [reference].[instruments] i
            ON i.[instrument_id] = tp.[instrument_id]
        INNER JOIN [reference].[exchanges] ex
            ON ex.[exchange_id] = i.[exchange_id]
        LEFT JOIN [thesis].[theses] t
            ON t.[thesis_id] = tp.[thesis_id]
        LEFT JOIN [intelligence].[signal_fusion_lineage] candidate_lineage
            ON candidate_lineage.[signal_id] = tp.[signal_id]
           AND candidate_lineage.[thesis_uid] = tp.[candidate_thesis_uid]
        LEFT JOIN [risk].[signal_risk_evaluations] sre
            ON sre.[signal_risk_evaluation_id] = tp.[signal_risk_evaluation_id]
        LEFT JOIN [risk].[risk_decisions] rd
            ON rd.[risk_decision_id] = COALESCE(tp.[risk_decision_id], sre.[risk_decision_id])
        OUTER APPLY
        (
            SELECT TOP (1)
                ec.[execution_command_id], ec.[execution_command_uid], ec.[broker_account_id],
                COALESCE(ecs.[current_status], 'PERSISTED') AS [command_status],
                COALESCE(ecs.[outcome_classification], 'NONE') AS [outcome_classification],
                ec.[generated_at_utc] AS [command_at_utc],
                ecs.[last_error_code], ecs.[last_error_message]
            FROM [execution].[execution_commands] ec
            LEFT JOIN [execution].[execution_command_states] ecs
                ON ecs.[execution_command_id] = ec.[execution_command_id]
            WHERE ec.[trade_plan_id] = tp.[trade_plan_id]
              AND ec.[environment] = 'PAPER'
              AND ec.[command_type] = 'PLACE'
            ORDER BY ec.[generated_at_utc] DESC, ec.[execution_command_id] DESC
        ) command_state
        LEFT JOIN [execution].[orders] o
            ON o.[place_execution_command_id] = command_state.[execution_command_id]
        OUTER APPLY
        (
            SELECT
                COUNT(1) AS [fill_count],
                SUM(f.[fill_quantity]) AS [total_fill_quantity],
                SUM(f.[fill_quantity] * f.[fill_price]) /
                    NULLIF(SUM(f.[fill_quantity]), 0) AS [weighted_average_fill_price],
                MAX(f.[fill_at_utc]) AS [last_fill_at_utc]
            FROM [execution].[fills] f
            WHERE f.[trade_plan_id] = tp.[trade_plan_id]
              AND f.[environment] = 'PAPER'
        ) fill_summary
        OUTER APPLY
        (
            SELECT TOP (1)
                pos.[position_id], pos.[portfolio_id], pos.[position_uid],
                pos.[status] AS [position_status], pos.[quantity] AS [position_quantity],
                pos.[average_open_price], pos.[realized_pnl_amount],
                pos.[unrealized_pnl_amount], pos.[updated_at_utc] AS [position_at_utc]
            FROM [execution].[fills] f
            INNER JOIN [portfolio].[position_events] pe
                ON pe.[fill_id] = f.[fill_id]
            INNER JOIN [portfolio].[positions] pos
                ON pos.[position_id] = pe.[position_id]
            WHERE f.[trade_plan_id] = tp.[trade_plan_id]
              AND f.[environment] = 'PAPER'
            ORDER BY pe.[event_at_utc] DESC, pe.[position_event_id] DESC
        ) position_link
        OUTER APPLY
        (
            SELECT TOP (1) p.[portfolio_id], p.[portfolio_code]
            FROM [portfolio].[portfolios] p
            WHERE p.[environment] = 'PAPER'
              AND p.[strategy_code] = s.[strategy_code]
              AND (position_link.[portfolio_id] IS NULL OR p.[portfolio_id] = position_link.[portfolio_id])
              AND (@portfolio_code IS NULL OR p.[portfolio_code] = @portfolio_code)
            ORDER BY
                CASE WHEN p.[portfolio_id] = position_link.[portfolio_id] THEN 0 ELSE 1 END,
                p.[portfolio_id]
        ) portfolio_route
        OUTER APPLY
        (
            SELECT TOP (1)
                ps.[pnl_snapshot_uid],
                psp.[net_pnl_amount] AS [position_net_pnl_amount],
                ps.[as_of_utc] AS [pnl_as_of_utc]
            FROM [portfolio].[pnl_snapshot_positions] psp
            INNER JOIN [portfolio].[pnl_snapshots] ps
                ON ps.[pnl_snapshot_id] = psp.[pnl_snapshot_id]
            WHERE psp.[position_id] = position_link.[position_id]
              AND ps.[portfolio_id] = position_link.[portfolio_id]
            ORDER BY ps.[as_of_utc] DESC, ps.[pnl_snapshot_id] DESC
        ) pnl_link
        WHERE tp.[environment] = 'PAPER'
          AND tp.[is_current] = 1
          AND (@correlation_uid IS NULL OR tp.[correlation_id] = @correlation_uid)
          AND (@portfolio_code IS NULL OR portfolio_route.[portfolio_code] = @portfolio_code)
        ORDER BY COALESCE(
            pnl_link.[pnl_as_of_utc],
            position_link.[position_at_utc],
            fill_summary.[last_fill_at_utc],
            o.[updated_at_utc],
            command_state.[command_at_utc],
            tp.[generated_at_utc]) DESC,
            tp.[trade_plan_id] DESC;
        """;

    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public async Task<IReadOnlyCollection<PaperTradeLifecycleSummaryV1>> ReadRecentAsync(
        string? portfolioCode,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(
            portfolioCode,
            maximumCount,
            null,
            cancellationToken);
        return rows.Select(BuildSummary).ToArray();
    }

    public async Task<PaperTradeLifecycleDetailV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken)
    {
        var row = (await ReadRowsAsync(
            portfolioCode,
            1,
            correlationUid,
            cancellationToken)).SingleOrDefault();
        if (row is null)
            return null;

        return new PaperTradeLifecycleDetailV1(
            PaperTradeLifecycleContractV1.ContractVersion,
            BuildSummary(row),
            BuildStages(row));
    }

    private async Task<IReadOnlyCollection<LifecycleRow>> ReadRowsAsync(
        string? portfolioCode,
        int maximumCount,
        Guid? correlationUid,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(LifecycleSql, connection)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
            string.IsNullOrWhiteSpace(portfolioCode) ? DBNull.Value : portfolioCode.Trim();
        command.Parameters.Add("@correlation_uid", SqlDbType.UniqueIdentifier).Value =
            correlationUid.HasValue ? correlationUid.Value : DBNull.Value;

        var rows = new List<LifecycleRow>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadRow(reader));
        return rows;
    }

    private static LifecycleRow ReadRow(SqlDataReader reader) => new(
        reader.GetGuid(0),
        ReadString(reader, 1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetGuid(5),
        reader.GetString(6),
        ReadUtc(reader, 7)!.Value,
        ReadGuid(reader, 8),
        ReadString(reader, 9),
        ReadUtc(reader, 10),
        ReadGuid(reader, 11),
        ReadString(reader, 12),
        ReadUtc(reader, 13),
        reader.GetGuid(14),
        reader.GetString(15),
        ReadUtc(reader, 16)!.Value,
        ReadGuid(reader, 17),
        ReadString(reader, 18),
        ReadString(reader, 19),
        ReadUtc(reader, 20),
        ReadString(reader, 21),
        ReadString(reader, 22),
        ReadGuid(reader, 23),
        ReadString(reader, 24),
        ReadDecimal(reader, 25),
        ReadDecimal(reader, 26),
        ReadDecimal(reader, 27),
        ReadUtc(reader, 28),
        reader.IsDBNull(29) ? 0 : reader.GetInt32(29),
        ReadDecimal(reader, 30),
        ReadDecimal(reader, 31),
        ReadUtc(reader, 32),
        ReadGuid(reader, 33),
        ReadString(reader, 34),
        ReadDecimal(reader, 35),
        ReadDecimal(reader, 36),
        ReadDecimal(reader, 37),
        ReadDecimal(reader, 38),
        ReadUtc(reader, 39),
        ReadGuid(reader, 40),
        ReadDecimal(reader, 41),
        ReadUtc(reader, 42));

    private PaperTradeLifecycleSummaryV1 BuildSummary(LifecycleRow row)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var lastActivityAtUtc = new DateTimeOffset?[]
        {
            row.SignalAtUtc,
            row.ThesisAtUtc,
            row.RiskAtUtc,
            row.TradePlanAtUtc,
            row.CommandAtUtc,
            row.OrderAtUtc,
            row.LastFillAtUtc,
            row.PositionAtUtc,
            row.PnlAsOfUtc,
        }.Where(value => value.HasValue).Max()!.Value;

        var warnings = BuildWarnings(row);
        var isComplete = row.PnlSnapshotUid.HasValue;
        var lifecycleStatus = ResolveLifecycleStatus(row, warnings, isComplete);
        var lifecycleStage = ResolveLifecycleStage(row);
        var isStale = !isComplete &&
            observedAtUtc - lastActivityAtUtc > TimeSpan.FromMinutes(readOptions.MaximumAgeMinutes);

        return new PaperTradeLifecycleSummaryV1(
            row.CorrelationUid,
            row.PortfolioCode,
            row.InstrumentKey,
            row.StrategyCode,
            row.Direction,
            lifecycleStage,
            lifecycleStatus,
            isComplete,
            isStale,
            lastActivityAtUtc,
            observedAtUtc,
            row.SignalUid,
            row.ThesisUid,
            row.RiskDecisionUid,
            row.TradePlanUid,
            row.ExecutionCommandUid,
            row.OrderUid,
            row.FillCount,
            row.PositionUid,
            row.PnlSnapshotUid,
            row.RequestedQuantity,
            row.TotalFillQuantity ?? row.OrderFilledQuantity,
            row.WeightedAverageFillPrice ?? row.OrderAverageFillPrice,
            row.PositionQuantity,
            row.PositionNetPnlAmount,
            warnings);
    }

    private static IReadOnlyCollection<PaperTradeLifecycleStageV1> BuildStages(LifecycleRow row) =>
    [
        new("SIGNAL", row.SignalStatus, row.SignalUid, row.SignalAtUtc, null, null),
        new("THESIS", row.ThesisStatus ?? "NOT_AVAILABLE", row.ThesisUid, row.ThesisAtUtc, null, null),
        new("RISK", row.RiskStatus ?? "NOT_AVAILABLE", row.RiskDecisionUid, row.RiskAtUtc, null, null),
        new("TRADE_PLAN", row.TradePlanStatus, row.TradePlanUid, row.TradePlanAtUtc, null, null),
        new("EXECUTION_COMMAND", row.CommandStatus ?? "NOT_STARTED", row.ExecutionCommandUid,
            row.CommandAtUtc, row.LastErrorCode, row.LastErrorMessage),
        new("ORDER", row.OrderStatus ?? "NOT_CREATED", row.OrderUid, row.OrderAtUtc, null, null),
        new("FILL", row.FillCount > 0 ? "FILLED" : "NOT_FILLED", null, row.LastFillAtUtc, null, null),
        new("POSITION", row.PositionStatus ?? "NOT_POSTED", row.PositionUid, row.PositionAtUtc, null, null),
        new("PNL", row.PnlSnapshotUid.HasValue ? "VALUED" : "NOT_VALUED",
            row.PnlSnapshotUid, row.PnlAsOfUtc, null, null),
    ];

    private static IReadOnlyCollection<string> BuildWarnings(LifecycleRow row)
    {
        var warnings = new List<string>();
        if (row.ThesisUid is null) warnings.Add("THESIS_LINEAGE_NOT_AVAILABLE");
        if (row.RiskDecisionUid is null) warnings.Add("RISK_DECISION_LINEAGE_NOT_AVAILABLE");
        if (row.ExecutionCommandUid.HasValue && row.OrderUid is null)
            warnings.Add("EXECUTION_COMMAND_HAS_NO_ORDER");
        if (row.FillCount > 0 && row.PositionUid is null)
            warnings.Add("FILL_HAS_NO_POSITION_PROJECTION");
        if (row.PositionUid.HasValue && row.PnlSnapshotUid is null)
            warnings.Add("POSITION_HAS_NO_PNL_VALUATION");
        if (!string.IsNullOrWhiteSpace(row.LastErrorCode))
            warnings.Add($"EXECUTION_ERROR:{row.LastErrorCode}");
        return warnings;
    }

    private static string ResolveLifecycleStatus(
        LifecycleRow row,
        IReadOnlyCollection<string> warnings,
        bool isComplete)
    {
        if (isComplete) return PaperTradeLifecycleContractV1.Complete;
        if (ContainsAny(row.RiskStatus, "REJECTED", "RESTRICTED") ||
            ContainsAny(row.TradePlanStatus, "REJECTED", "EXPIRED"))
            return PaperTradeLifecycleContractV1.Rejected;
        if (ContainsAny(row.CommandStatus, "FAILED", "REJECTED", "EXPIRED") ||
            ContainsAny(row.OrderStatus, "FAILED", "REJECTED", "EXPIRED"))
            return PaperTradeLifecycleContractV1.Failed;
        if (warnings.Any(item => item.Contains("LINEAGE_NOT_AVAILABLE", StringComparison.Ordinal) ||
                                 item.Contains("HAS_NO_", StringComparison.Ordinal)))
            return PaperTradeLifecycleContractV1.PartialLineage;
        return PaperTradeLifecycleContractV1.InProgress;
    }

    private static string ResolveLifecycleStage(LifecycleRow row)
    {
        if (row.PnlSnapshotUid.HasValue) return "PNL_VALUED";
        if (row.PositionUid.HasValue) return "POSITION_POSTED";
        if (row.FillCount > 0) return "FILLED";
        if (row.OrderUid.HasValue) return "ORDER_CREATED";
        if (row.ExecutionCommandUid.HasValue) return "EXECUTION_AUTHORIZED";
        if (row.TradePlanUid != Guid.Empty) return "TRADE_PLAN_READY";
        if (row.RiskDecisionUid.HasValue) return "RISK_EVALUATED";
        if (row.ThesisUid.HasValue) return "THESIS_CREATED";
        return "SIGNAL_CREATED";
    }

    private static bool ContainsAny(string? value, params string[] candidates) =>
        value is not null && candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? ReadGuid(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static decimal? ReadDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? ReadUtc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record LifecycleRow(
        Guid CorrelationUid,
        string? PortfolioCode,
        string InstrumentKey,
        string StrategyCode,
        string Direction,
        Guid SignalUid,
        string SignalStatus,
        DateTimeOffset SignalAtUtc,
        Guid? ThesisUid,
        string? ThesisStatus,
        DateTimeOffset? ThesisAtUtc,
        Guid? RiskDecisionUid,
        string? RiskStatus,
        DateTimeOffset? RiskAtUtc,
        Guid TradePlanUid,
        string TradePlanStatus,
        DateTimeOffset TradePlanAtUtc,
        Guid? ExecutionCommandUid,
        string? CommandStatus,
        string? OutcomeClassification,
        DateTimeOffset? CommandAtUtc,
        string? LastErrorCode,
        string? LastErrorMessage,
        Guid? OrderUid,
        string? OrderStatus,
        decimal? RequestedQuantity,
        decimal? OrderFilledQuantity,
        decimal? OrderAverageFillPrice,
        DateTimeOffset? OrderAtUtc,
        int FillCount,
        decimal? TotalFillQuantity,
        decimal? WeightedAverageFillPrice,
        DateTimeOffset? LastFillAtUtc,
        Guid? PositionUid,
        string? PositionStatus,
        decimal? PositionQuantity,
        decimal? AverageOpenPrice,
        decimal? RealizedPnlAmount,
        decimal? UnrealizedPnlAmount,
        DateTimeOffset? PositionAtUtc,
        Guid? PnlSnapshotUid,
        decimal? PositionNetPnlAmount,
        DateTimeOffset? PnlAsOfUtc);
}

public static class PaperTradeLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapPaperTradeLifecycleEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/execution/lifecycles", async (
            string? portfolioCode,
            int? limit,
            IPaperTradeLifecycleReadStore store,
            PaperTradeLifecycleReadOptions options,
            CancellationToken cancellationToken) =>
        {
            if (!store.IsAvailable)
            {
                return Results.Problem(
                    title: "PAPER lifecycle read model unavailable",
                    detail: store.UnavailableReason,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var boundedLimit = Math.Clamp(
                limit ?? options.DefaultLimit,
                1,
                options.MaximumLimit);
            var normalizedPortfolioCode = string.IsNullOrWhiteSpace(portfolioCode)
                ? null
                : portfolioCode.Trim();
            var observedAtUtc = DateTimeOffset.UtcNow;
            var items = await store.ReadRecentAsync(
                normalizedPortfolioCode,
                boundedLimit,
                cancellationToken);
            return Results.Ok(new PaperTradeLifecycleListV1(
                PaperTradeLifecycleContractV1.ContractVersion,
                PaperTradeLifecycleContractV1.PaperEnvironment,
                normalizedPortfolioCode,
                boundedLimit,
                observedAtUtc,
                items));
        });

        endpoints.MapGet("/api/v1/execution/lifecycles/{correlationUid:guid}", async (
            Guid correlationUid,
            string? portfolioCode,
            IPaperTradeLifecycleReadStore store,
            CancellationToken cancellationToken) =>
        {
            if (!store.IsAvailable)
            {
                return Results.Problem(
                    title: "PAPER lifecycle read model unavailable",
                    detail: store.UnavailableReason,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var lifecycle = await store.ReadAsync(
                correlationUid,
                string.IsNullOrWhiteSpace(portfolioCode) ? null : portfolioCode.Trim(),
                cancellationToken);
            return lifecycle is null ? Results.NotFound() : Results.Ok(lifecycle);
        });

        return endpoints;
    }
}
