using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Risk.Service;

public sealed record CanonicalPortfolioRiskState(
    Guid RiskSnapshotUid,
    string OperatingMode,
    decimal EffectiveRiskMultiplier,
    bool NewExposureAllowed,
    decimal PortfolioDrawdownPercent,
    DateTimeOffset SourceAsOfUtc);

public interface ICanonicalPortfolioRiskStateStore
{
    Task<CanonicalPortfolioRiskState?> ReadLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken);
}

public sealed class SqlServerCanonicalPortfolioRiskStateStore(
    SignalRiskPersistenceOptions options) : ICanonicalPortfolioRiskStateStore
{
    public async Task<CanonicalPortfolioRiskState?> ReadLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                state.risk_snapshot_uid,
                state.operating_mode,
                state.effective_risk_multiplier,
                state.new_exposure_allowed,
                snapshot.portfolio_drawdown_fraction,
                state.source_as_of_utc
            FROM risk.portfolio_control_states state
            INNER JOIN risk.portfolio_risk_snapshots snapshot
                ON snapshot.risk_snapshot_uid = state.risk_snapshot_uid
            WHERE state.portfolio_code = @portfolio_code
              AND state.environment = 'PAPER';
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = portfolioCode;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new CanonicalPortfolioRiskState(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetDecimal(2),
            reader.GetBoolean(3),
            reader.GetDecimal(4) * 100m,
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
    }
}
