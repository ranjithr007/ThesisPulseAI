using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerPortfolioRiskEvaluationContextStore(
    SignalRiskPersistenceOptions persistenceOptions) : IPortfolioRiskEvaluationContextStore
{
    public async Task<PortfolioRiskEvaluationInputV1?> ReadAsync(
        AutomaticPortfolioRiskWorkItem workItem,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @snapshot_date date;
            DECLARE @week_start date;

            SELECT @snapshot_date = CONVERT(date, ps.as_of_utc)
            FROM portfolio.pnl_snapshots ps
            WHERE ps.pnl_snapshot_uid = @source_uid;

            SET @week_start = DATEADD(day,
                -(DATEDIFF(day, CONVERT(date, '19000101'), @snapshot_date) % 7),
                @snapshot_date);

            SELECT
                ps.pnl_snapshot_uid,
                p.portfolio_code,
                p.environment,
                RTRIM(ps.currency_code),
                COALESCE(day_pnl.amount, 0) + ps.unrealized_pnl_amount,
                COALESCE(week_pnl.amount, 0) + ps.unrealized_pnl_amount,
                ps.net_liquidation_value_amount,
                ps.strategy_drawdown_fraction,
                ps.portfolio_drawdown_fraction,
                ps.as_of_utc,
                policy.risk_policy_uid,
                policy.risk_policy_version,
                policy.environment,
                policy.daily_soft_loss_fraction,
                policy.daily_hard_loss_fraction,
                policy.weekly_loss_fraction,
                policy.maximum_strategy_drawdown_fraction,
                policy.maximum_portfolio_drawdown_fraction,
                policy.soft_risk_multiplier,
                policy.hard_operating_mode
            FROM portfolio.pnl_snapshots ps
            INNER JOIN portfolio.portfolios p ON p.portfolio_id = ps.portfolio_id
            INNER JOIN risk.risk_policies policy
                ON policy.risk_policy_uid = @policy_uid
               AND policy.risk_policy_version = @policy_version
            OUTER APPLY
            (
                SELECT SUM(entry.net_realized_pnl_amount) AS amount
                FROM portfolio.realized_pnl_entries entry
                WHERE entry.portfolio_id = ps.portfolio_id
                  AND entry.trade_date = @snapshot_date
                  AND entry.recognized_at_utc <= ps.as_of_utc
            ) day_pnl
            OUTER APPLY
            (
                SELECT SUM(entry.net_realized_pnl_amount) AS amount
                FROM portfolio.realized_pnl_entries entry
                WHERE entry.portfolio_id = ps.portfolio_id
                  AND entry.trade_date >= @week_start
                  AND entry.trade_date <= @snapshot_date
                  AND entry.recognized_at_utc <= ps.as_of_utc
            ) week_pnl
            WHERE ps.pnl_snapshot_uid = @source_uid
              AND p.portfolio_code = @portfolio_code
              AND p.environment = @environment
              AND policy.environment = @environment
              AND policy.initial_status IN ('APPROVED', 'ACTIVE')
              AND EXISTS
              (
                  SELECT 1
                  FROM risk.active_policy_assignments assignment
                  WHERE assignment.risk_policy_id = policy.risk_policy_id
                    AND assignment.environment = @environment
                    AND assignment.scope_type = 'GLOBAL'
                    AND assignment.assignment_status = 'ACTIVE'
                    AND assignment.active_from_utc <= ps.as_of_utc
                    AND (assignment.active_to_utc IS NULL OR assignment.active_to_utc > ps.as_of_utc)
              );
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@source_uid", SqlDbType.UniqueIdentifier).Value = workItem.SourcePnlSnapshotUid;
        command.Parameters.Add("@policy_uid", SqlDbType.UniqueIdentifier).Value = workItem.PolicyUid;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = workItem.PolicyVersion;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = workItem.PortfolioCode;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 30).Value = workItem.Environment;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var sourceAsOfUtc = ReadUtc(reader, 9);
        if (sourceAsOfUtc != workItem.SourceAsOfUtc)
            return null;

        var policy = new PortfolioRiskPolicyV1(
            reader.GetGuid(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetDecimal(13),
            reader.GetDecimal(14),
            reader.GetDecimal(15),
            reader.GetDecimal(16),
            reader.GetDecimal(17),
            reader.GetDecimal(18),
            reader.GetString(19));

        return new PortfolioRiskEvaluationInputV1(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8),
            sourceAsOfUtc,
            DateTimeOffset.UtcNow,
            policy);
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
