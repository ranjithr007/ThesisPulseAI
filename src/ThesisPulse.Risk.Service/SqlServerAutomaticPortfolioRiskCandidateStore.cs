using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerAutomaticPortfolioRiskCandidateStore(
    SignalRiskPersistenceOptions persistenceOptions,
    AutomaticPortfolioRiskOptions riskOptions) : IAutomaticPortfolioRiskCandidateStore
{
    public async Task<IReadOnlyCollection<AutomaticPortfolioRiskCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                ps.pnl_snapshot_uid,
                policy.risk_policy_uid,
                policy.risk_policy_version,
                p.portfolio_code,
                p.environment,
                ps.as_of_utc
            FROM portfolio.pnl_snapshots ps WITH (READPAST)
            INNER JOIN portfolio.portfolios p ON p.portfolio_id = ps.portfolio_id
            INNER JOIN risk.active_policy_assignments assignment
                ON assignment.environment = p.environment
               AND assignment.scope_type = 'GLOBAL'
               AND assignment.scope_id = @scope_id
               AND assignment.assignment_status = 'ACTIVE'
               AND assignment.active_from_utc <= ps.as_of_utc
               AND (assignment.active_to_utc IS NULL OR assignment.active_to_utc > ps.as_of_utc)
            INNER JOIN risk.risk_policies policy
                ON policy.risk_policy_id = assignment.risk_policy_id
               AND policy.initial_status IN ('APPROVED', 'ACTIVE')
               AND policy.effective_from_utc <= ps.as_of_utc
               AND (policy.effective_to_utc IS NULL OR policy.effective_to_utc > ps.as_of_utc)
            WHERE p.environment = @environment
              AND p.status IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY')
              AND NOT EXISTS
              (
                  SELECT 1 FROM risk.portfolio_risk_snapshots existing
                  WHERE existing.source_pnl_snapshot_uid = ps.pnl_snapshot_uid
                    AND existing.policy_uid = policy.risk_policy_uid
                    AND existing.policy_version = policy.risk_policy_version
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM risk.portfolio_risk_work_items work
                  WHERE work.source_pnl_snapshot_uid = ps.pnl_snapshot_uid
                    AND work.policy_uid = policy.risk_policy_uid
                    AND work.policy_version = policy.risk_policy_version
              )
            ORDER BY ps.as_of_utc, ps.pnl_snapshot_id;
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 30).Value = riskOptions.Environment;
        command.Parameters.Add("@scope_id", SqlDbType.VarChar, 200).Value = riskOptions.GlobalPolicyScopeId;

        var results = new List<AutomaticPortfolioRiskCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sourceUid = reader.GetGuid(0);
            var policyUid = reader.GetGuid(1);
            var policyVersion = reader.GetString(2);
            results.Add(new AutomaticPortfolioRiskCandidate(
                RequestUid(sourceUid, policyUid, policyVersion),
                sourceUid,
                policyUid,
                policyVersion,
                reader.GetString(3),
                reader.GetString(4),
                ReadUtc(reader, 5)));
        }
        return results;
    }

    private static Guid RequestUid(Guid sourceUid, Guid policyUid, string policyVersion)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"portfolio-risk:{sourceUid:N}:{policyUid:N}:{policyVersion}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
