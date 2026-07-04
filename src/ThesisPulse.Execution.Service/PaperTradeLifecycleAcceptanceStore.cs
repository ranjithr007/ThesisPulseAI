using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public interface IPaperTradeLifecycleAcceptanceStore
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    Task<PaperTradeLifecycleAcceptanceReportV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken);
}

public sealed class UnavailablePaperTradeLifecycleAcceptanceStore : IPaperTradeLifecycleAcceptanceStore
{
    public bool IsAvailable => false;
    public string? UnavailableReason =>
        "PAPER lifecycle acceptance requires SQL Server execution persistence.";

    public Task<PaperTradeLifecycleAcceptanceReportV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken) =>
        Task.FromResult<PaperTradeLifecycleAcceptanceReportV1?>(null);
}

public sealed class SqlServerPaperTradeLifecycleAcceptanceStore(
    SqlServerPaperExecutionLedgerOptions ledgerOptions,
    IPaperTradeLifecycleReadStore lifecycleStore,
    PaperTradeLifecycleAcceptanceEvaluator evaluator) : IPaperTradeLifecycleAcceptanceStore
{
    private const string LineageSql = """
        WITH root AS
        (
            SELECT TOP (1)
                tp.trade_plan_id,
                tp.signal_id,
                tp.thesis_id,
                tp.candidate_thesis_uid,
                tp.risk_decision_id,
                tp.signal_risk_evaluation_id
            FROM [risk].[trade_plans] tp WITH (READPAST)
            WHERE tp.environment = 'PAPER'
              AND tp.is_current = 1
              AND tp.correlation_id = @correlation_uid
            ORDER BY tp.generated_at_utc DESC, tp.trade_plan_id DESC
        ),
        command_link AS
        (
            SELECT TOP (1) ec.*
            FROM [execution].[execution_commands] ec WITH (READPAST)
            INNER JOIN root r ON r.trade_plan_id = ec.trade_plan_id
            WHERE ec.environment = 'PAPER' AND ec.command_type = 'PLACE'
            ORDER BY ec.generated_at_utc DESC, ec.execution_command_id DESC
        ),
        order_event_link AS
        (
            SELECT TOP (1) oe.*
            FROM [execution].[order_events] oe WITH (READPAST)
            INNER JOIN root r ON r.trade_plan_id = oe.trade_plan_id
            WHERE oe.environment = 'PAPER'
            ORDER BY oe.event_at_utc DESC, oe.order_event_id DESC
        ),
        fill_link AS
        (
            SELECT TOP (1) f.*
            FROM [execution].[fills] f WITH (READPAST)
            INNER JOIN root r ON r.trade_plan_id = f.trade_plan_id
            WHERE f.environment = 'PAPER'
            ORDER BY f.fill_at_utc DESC, f.fill_id DESC
        ),
        position_event_link AS
        (
            SELECT TOP (1) pe.*
            FROM [portfolio].[position_events] pe WITH (READPAST)
            INNER JOIN [execution].[fills] f ON f.fill_id = pe.fill_id
            INNER JOIN root r ON r.trade_plan_id = f.trade_plan_id
            ORDER BY pe.event_at_utc DESC, pe.position_event_id DESC
        ),
        pnl_link AS
        (
            SELECT TOP (1) ps.*
            FROM [portfolio].[pnl_snapshot_positions] psp WITH (READPAST)
            INNER JOIN [portfolio].[pnl_snapshots] ps
                ON ps.pnl_snapshot_id = psp.pnl_snapshot_id
            INNER JOIN [portfolio].[position_events] pe
                ON pe.position_id = psp.position_id
            INNER JOIN [execution].[fills] f
                ON f.fill_id = pe.fill_id
            INNER JOIN root r ON r.trade_plan_id = f.trade_plan_id
            ORDER BY ps.as_of_utc DESC, ps.pnl_snapshot_id DESC
        ),
        evidence AS
        (
            SELECT 1 AS stage_order, 'SIGNAL' AS stage, s.signal_uid AS entity_uid,
                   s.correlation_id, s.causation_id, s.generated_at_utc AS occurred_at_utc,
                   'intelligence.signals' AS source_table
            FROM [intelligence].[signals] s
            INNER JOIN root r ON r.signal_id = s.signal_id

            UNION ALL

            SELECT 2, 'THESIS', t.thesis_uid, t.correlation_id, t.causation_id,
                   t.generated_at_utc, 'thesis.theses'
            FROM [thesis].[theses] t
            INNER JOIN root r ON r.thesis_id = t.thesis_id

            UNION ALL

            SELECT 2, 'THESIS', lineage.thesis_uid, s.correlation_id,
                   lineage.thesis_request_uid, lineage.created_at_utc,
                   'intelligence.signal_fusion_lineage'
            FROM [intelligence].[signal_fusion_lineage] lineage
            INNER JOIN root r
                ON r.signal_id = lineage.signal_id
               AND r.candidate_thesis_uid = lineage.thesis_uid
            INNER JOIN [intelligence].[signals] s
                ON s.signal_id = lineage.signal_id

            UNION ALL

            SELECT 3, 'RISK', rd.risk_decision_uid, rd.correlation_id, rd.causation_id,
                   rd.evaluated_at_utc, 'risk.risk_decisions'
            FROM [risk].[risk_decisions] rd
            INNER JOIN root r ON r.risk_decision_id = rd.risk_decision_id

            UNION ALL

            SELECT 3, 'RISK', sre.risk_decision_uid, sre.correlation_id, sre.causation_id,
                   sre.updated_at_utc, 'risk.signal_risk_evaluations'
            FROM [risk].[signal_risk_evaluations] sre
            INNER JOIN root r
                ON r.signal_risk_evaluation_id = sre.signal_risk_evaluation_id
            WHERE sre.risk_decision_uid IS NOT NULL

            UNION ALL

            SELECT 4, 'TRADE_PLAN', tp.trade_plan_uid, tp.correlation_id, tp.causation_id,
                   tp.generated_at_utc, 'risk.trade_plans'
            FROM [risk].[trade_plans] tp
            INNER JOIN root r ON r.trade_plan_id = tp.trade_plan_id

            UNION ALL

            SELECT 5, 'EXECUTION_COMMAND', ec.execution_command_uid, ec.correlation_id,
                   ec.causation_id, ec.generated_at_utc, 'execution.execution_commands'
            FROM command_link ec

            UNION ALL

            SELECT 6, 'ORDER_EVENT', oe.order_event_uid, oe.correlation_id,
                   oe.causation_id, oe.event_at_utc, 'execution.order_events'
            FROM order_event_link oe

            UNION ALL

            SELECT 7, 'FILL', f.fill_uid, f.correlation_id, f.causation_id,
                   f.fill_at_utc, 'execution.fills'
            FROM fill_link f

            UNION ALL

            SELECT 8, 'POSITION_EVENT', pe.position_event_uid, pe.correlation_id,
                   pe.causation_id, pe.event_at_utc, 'portfolio.position_events'
            FROM position_event_link pe

            UNION ALL

            SELECT 9, 'PNL', ps.pnl_snapshot_uid, ps.correlation_id,
                   CAST(NULL AS uniqueidentifier), ps.as_of_utc, 'portfolio.pnl_snapshots'
            FROM pnl_link ps
        )
        SELECT stage, entity_uid, correlation_id, causation_id, occurred_at_utc, source_table
        FROM evidence
        ORDER BY stage_order;
        """;

    private const string OperationalStateSql = """
        WITH account_references AS
        (
            SELECT DISTINCT ba.account_reference
            FROM [execution].[execution_commands] ec WITH (READPAST)
            INNER JOIN [broker].[broker_accounts] ba
                ON ba.broker_account_id = ec.broker_account_id
            WHERE ec.environment = 'PAPER'
              AND ec.correlation_id = @correlation_uid
        )
        SELECT
            state.scope_type,
            state.scope_id,
            state.effective_operating_mode,
            control.control_uid,
            state.allows_new_exposure,
            state.allows_risk_reducing_exits,
            state.requires_operator_review,
            state.evaluated_at_utc,
            state.evaluation_version
        FROM [operations].[scope_operating_states] state WITH (READPAST)
        LEFT JOIN [operations].[operational_controls] control
            ON control.operational_control_id = state.source_operational_control_id
        WHERE state.environment = 'PAPER'
          AND
          (
              state.scope_type = 'PLATFORM'
              OR (state.scope_type = 'ENVIRONMENT' AND state.scope_id IN ('PAPER', '*'))
              OR (state.scope_type = 'STRATEGY' AND state.scope_id = @strategy_code)
              OR (state.scope_type = 'INSTRUMENT' AND state.scope_id = @instrument_key)
              OR (state.scope_type = 'BROKER_ACCOUNT'
                  AND state.scope_id IN (SELECT account_reference FROM account_references))
          )
        ORDER BY
            CASE state.effective_operating_mode
                WHEN 'HALTED' THEN 0
                WHEN 'PAUSED' THEN 1
                WHEN 'CLOSE_ONLY' THEN 2
                WHEN 'RESTRICTED' THEN 3
                WHEN 'RECOVERY' THEN 4
                ELSE 5
            END,
            state.scope_type,
            state.scope_id;
        """;

    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public async Task<PaperTradeLifecycleAcceptanceReportV1?> ReadAsync(
        Guid correlationUid,
        string? portfolioCode,
        CancellationToken cancellationToken)
    {
        var normalizedPortfolioCode = string.IsNullOrWhiteSpace(portfolioCode)
            ? null
            : portfolioCode.Trim();
        var lifecycle = await lifecycleStore.ReadAsync(
            correlationUid,
            normalizedPortfolioCode,
            cancellationToken);
        if (lifecycle is null)
            return null;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var lineage = await ReadLineageAsync(connection, correlationUid, cancellationToken);
        var operationalStates = await ReadOperationalStatesAsync(
            connection,
            lifecycle.Summary,
            cancellationToken);

        return evaluator.Evaluate(
            PaperTradeLifecycleContractV1.PaperEnvironment,
            lifecycle,
            lineage,
            operationalStates,
            DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyCollection<PaperTradeLifecycleLineageEvidenceV1>> ReadLineageAsync(
        SqlConnection connection,
        Guid correlationUid,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(LineageSql, connection)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@correlation_uid", SqlDbType.UniqueIdentifier).Value = correlationUid;

        var evidence = new List<PaperTradeLifecycleLineageEvidenceV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(new PaperTradeLifecycleLineageEvidenceV1(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                ReadUtc(reader, 4),
                reader.GetString(5)));
        }
        return evidence;
    }

    private async Task<IReadOnlyCollection<PaperTradeLifecycleOperationalStateV1>>
        ReadOperationalStatesAsync(
            SqlConnection connection,
            PaperTradeLifecycleSummaryV1 summary,
            CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(OperationalStateSql, connection)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@correlation_uid", SqlDbType.UniqueIdentifier).Value =
            summary.CorrelationUid;
        command.Parameters.Add("@strategy_code", SqlDbType.VarChar, 100).Value =
            summary.StrategyCode;
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            summary.InstrumentKey;

        var states = new List<PaperTradeLifecycleOperationalStateV1>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new PaperTradeLifecycleOperationalStateV1(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                ReadUtc(reader, 7),
                reader.GetString(8)));
        }
        return states;
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
