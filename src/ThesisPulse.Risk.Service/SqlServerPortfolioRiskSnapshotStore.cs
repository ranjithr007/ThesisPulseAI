using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerPortfolioRiskSnapshotStore(
    SignalRiskPersistenceOptions options) : IPortfolioRiskSnapshotStore
{
    public async Task<PortfolioRiskPersistResult> PersistAsync(
        PortfolioRiskStateSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existingUid = await ReadExistingSnapshotUidAsync(connection, transaction, snapshot, cancellationToken);
            if (existingUid is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PortfolioRiskPersistResult(
                    AutomaticPortfolioRiskStatus.Duplicate,
                    existingUid.Value,
                    0,
                    Array.Empty<string>());
            }

            var previousMode = await ReadPreviousModeAsync(connection, transaction, snapshot, cancellationToken);
            await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken);
            var version = await UpsertControlStateAsync(connection, transaction, snapshot, cancellationToken);
            await InsertEventAsync(connection, transaction, snapshot, previousMode, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PortfolioRiskPersistResult(
                AutomaticPortfolioRiskStatus.Evaluated,
                snapshot.RiskSnapshotUid,
                version,
                snapshot.Reasons);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PortfolioRiskPersistResult(
                AutomaticPortfolioRiskStatus.Duplicate,
                snapshot.RiskSnapshotUid,
                0,
                Array.Empty<string>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<Guid?> ReadExistingSnapshotUidAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRiskStateSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [risk_snapshot_uid]
            FROM [risk].[portfolio_risk_snapshots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [risk_snapshot_uid] = @risk_snapshot_uid
               OR ([source_pnl_snapshot_uid] = @source_pnl_snapshot_uid
                   AND [policy_uid] = @policy_uid
                   AND [policy_version] = @policy_version);
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.RiskSnapshotUid;
        command.Parameters.Add("@source_pnl_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.SourcePnlSnapshotUid;
        command.Parameters.Add("@policy_uid", SqlDbType.UniqueIdentifier).Value = snapshot.PolicyUid;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = snapshot.PolicyVersion;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid value ? value : null;
    }

    private static async Task<string?> ReadPreviousModeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRiskStateSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [operating_mode]
            FROM [risk].[portfolio_control_states] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_code] = @portfolio_code
              AND [environment] = @environment;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = snapshot.PortfolioCode;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = snapshot.Environment;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task InsertSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRiskStateSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[portfolio_risk_snapshots]
            (
                [risk_snapshot_uid], [source_pnl_snapshot_uid], [policy_uid], [policy_version],
                [portfolio_code], [environment], [currency_code], [operating_mode],
                [effective_risk_multiplier], [daily_pnl_amount], [weekly_pnl_amount],
                [daily_loss_fraction], [weekly_loss_fraction], [strategy_drawdown_fraction],
                [portfolio_drawdown_fraction], [new_exposure_allowed],
                [risk_reducing_exit_allowed], [reasons_json], [source_as_of_utc], [evaluated_at_utc]
            )
            VALUES
            (
                @risk_snapshot_uid, @source_pnl_snapshot_uid, @policy_uid, @policy_version,
                @portfolio_code, @environment, @currency_code, @operating_mode,
                @effective_risk_multiplier, @daily_pnl_amount, @weekly_pnl_amount,
                @daily_loss_fraction, @weekly_loss_fraction, @strategy_drawdown_fraction,
                @portfolio_drawdown_fraction, @new_exposure_allowed,
                @risk_reducing_exit_allowed, @reasons_json, @source_as_of_utc, @evaluated_at_utc
            );
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        AddSnapshotParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> UpsertControlStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRiskStateSnapshotV1 snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE [risk].[portfolio_control_states] WITH (HOLDLOCK) AS target
            USING (SELECT @portfolio_code AS [portfolio_code], @environment AS [environment]) AS source
            ON target.[portfolio_code] = source.[portfolio_code]
               AND target.[environment] = source.[environment]
            WHEN MATCHED AND target.[source_as_of_utc] < @source_as_of_utc THEN
                UPDATE SET
                    [risk_snapshot_uid] = @risk_snapshot_uid,
                    [operating_mode] = @operating_mode,
                    [effective_risk_multiplier] = @effective_risk_multiplier,
                    [new_exposure_allowed] = @new_exposure_allowed,
                    [risk_reducing_exit_allowed] = @risk_reducing_exit_allowed,
                    [source_as_of_utc] = @source_as_of_utc,
                    [version_number] = target.[version_number] + 1,
                    [updated_at_utc] = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT
                (
                    [portfolio_code], [environment], [risk_snapshot_uid], [operating_mode],
                    [effective_risk_multiplier], [new_exposure_allowed],
                    [risk_reducing_exit_allowed], [source_as_of_utc], [version_number]
                )
                VALUES
                (
                    @portfolio_code, @environment, @risk_snapshot_uid, @operating_mode,
                    @effective_risk_multiplier, @new_exposure_allowed,
                    @risk_reducing_exit_allowed, @source_as_of_utc, 1
                )
            OUTPUT INSERTED.[version_number];
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        AddControlParameters(command, snapshot);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
            throw new InvalidOperationException("Stale portfolio risk snapshot cannot replace current control state.");
        return Convert.ToInt64(result);
    }

    private static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PortfolioRiskStateSnapshotV1 snapshot,
        string? previousMode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[portfolio_risk_events]
            (
                [event_uid], [risk_snapshot_uid], [portfolio_code], [environment],
                [previous_operating_mode], [operating_mode], [reasons_json], [occurred_at_utc]
            )
            VALUES
            (
                @event_uid, @risk_snapshot_uid, @portfolio_code, @environment,
                @previous_operating_mode, @operating_mode, @reasons_json, @occurred_at_utc
            );
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value = CreateEventUid(snapshot.RiskSnapshotUid);
        command.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.RiskSnapshotUid;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = snapshot.PortfolioCode;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = snapshot.Environment;
        command.Parameters.Add("@previous_operating_mode", SqlDbType.VarChar, 30).Value =
            (object?)previousMode ?? DBNull.Value;
        command.Parameters.Add("@operating_mode", SqlDbType.VarChar, 30).Value = snapshot.OperatingMode;
        command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(snapshot.Reasons);
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value = snapshot.EvaluatedAtUtc.UtcDateTime;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSnapshotParameters(SqlCommand command, PortfolioRiskStateSnapshotV1 snapshot)
    {
        AddControlParameters(command, snapshot);
        command.Parameters.Add("@source_pnl_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.SourcePnlSnapshotUid;
        command.Parameters.Add("@policy_uid", SqlDbType.UniqueIdentifier).Value = snapshot.PolicyUid;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = snapshot.PolicyVersion;
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = snapshot.CurrencyCode;
        command.Parameters.Add("@daily_pnl_amount", SqlDbType.Decimal).Value = snapshot.DailyPnlAmount;
        command.Parameters.Add("@weekly_pnl_amount", SqlDbType.Decimal).Value = snapshot.WeeklyPnlAmount;
        command.Parameters.Add("@daily_loss_fraction", SqlDbType.Decimal).Value = snapshot.DailyLossFraction;
        command.Parameters.Add("@weekly_loss_fraction", SqlDbType.Decimal).Value = snapshot.WeeklyLossFraction;
        command.Parameters.Add("@strategy_drawdown_fraction", SqlDbType.Decimal).Value = snapshot.StrategyDrawdownFraction;
        command.Parameters.Add("@portfolio_drawdown_fraction", SqlDbType.Decimal).Value = snapshot.PortfolioDrawdownFraction;
        command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(snapshot.Reasons);
        command.Parameters.Add("@evaluated_at_utc", SqlDbType.DateTime2).Value = snapshot.EvaluatedAtUtc.UtcDateTime;
    }

    private static void AddControlParameters(SqlCommand command, PortfolioRiskStateSnapshotV1 snapshot)
    {
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = snapshot.PortfolioCode;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = snapshot.Environment;
        command.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.RiskSnapshotUid;
        command.Parameters.Add("@operating_mode", SqlDbType.VarChar, 30).Value = snapshot.OperatingMode;
        command.Parameters.Add("@effective_risk_multiplier", SqlDbType.Decimal).Value = snapshot.EffectiveRiskMultiplier;
        command.Parameters.Add("@new_exposure_allowed", SqlDbType.Bit).Value = snapshot.NewExposureAllowed;
        command.Parameters.Add("@risk_reducing_exit_allowed", SqlDbType.Bit).Value = snapshot.RiskReducingExitAllowed;
        command.Parameters.Add("@source_as_of_utc", SqlDbType.DateTime2).Value = snapshot.SourceAsOfUtc.UtcDateTime;
    }

    private static Guid CreateEventUid(Guid snapshotUid)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"portfolio-risk-event:{snapshotUid:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
