using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed class SqlServerPortfolioRiskSnapshotStore(
    SignalRiskPersistenceOptions options) : IPortfolioRiskSnapshotStore
{
    public async Task<PortfolioRiskPersistResult> PersistAsync(
        PortfolioRiskSnapshotV1 snapshot,
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
            const string existingSql = """
                SELECT TOP (1) [risk_snapshot_uid]
                FROM [risk].[portfolio_risk_snapshots] WITH (UPDLOCK, HOLDLOCK)
                WHERE [risk_snapshot_uid] = @risk_snapshot_uid
                   OR ([source_pnl_snapshot_uid] = @source_pnl_snapshot_uid
                       AND [policy_uid] = @policy_uid
                       AND [policy_version] = @policy_version);
                """;
            await using (var existing = Command(connection, transaction, existingSql))
            {
                AddIdentityParameters(existing, snapshot);
                var found = await existing.ExecuteScalarAsync(cancellationToken);
                if (found is Guid existingUid)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new PortfolioRiskPersistResult(
                        AutomaticPortfolioRiskStatus.Duplicate,
                        existingUid,
                        0,
                        Array.Empty<string>());
                }
            }

            const string previousModeSql = """
                SELECT [operating_mode]
                FROM [risk].[portfolio_control_states] WITH (UPDLOCK, HOLDLOCK)
                WHERE [portfolio_code] = @portfolio_code
                  AND [environment] = @environment;
                """;
            string? previousMode;
            await using (var previous = Command(connection, transaction, previousModeSql))
            {
                previous.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = snapshot.PortfolioCode;
                previous.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = snapshot.Environment;
                previousMode = await previous.ExecuteScalarAsync(cancellationToken) as string;
            }

            const string snapshotSql = """
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
            await using (var insert = Command(connection, transaction, snapshotSql))
            {
                AddSnapshotParameters(insert, snapshot);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            const string stateSql = """
                MERGE [risk].[portfolio_control_states] WITH (HOLDLOCK) AS target
                USING
                (
                    SELECT @portfolio_code AS [portfolio_code], @environment AS [environment]
                ) AS source
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
            long version;
            await using (var state = Command(connection, transaction, stateSql))
            {
                AddControlParameters(state, snapshot);
                var result = await state.ExecuteScalarAsync(cancellationToken);
                if (result is null)
                    throw new InvalidOperationException("Stale portfolio risk snapshot cannot replace current control state.");
                version = Convert.ToInt64(result);
            }

            const string eventSql = """
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
            await using (var eventCommand = Command(connection, transaction, eventSql))
            {
                eventCommand.Parameters.Add("@event_uid", SqlDbType.UniqueIdentifier).Value =
                    CreateEventUid(snapshot.RiskSnapshotUid);
                eventCommand.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value =
                    snapshot.RiskSnapshotUid;
                eventCommand.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = snapshot.PortfolioCode;
                eventCommand.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = snapshot.Environment;
                eventCommand.Parameters.Add("@previous_operating_mode", SqlDbType.VarChar, 30).Value =
                    (object?)previousMode ?? DBNull.Value;
                eventCommand.Parameters.Add("@operating_mode", SqlDbType.VarChar, 30).Value = snapshot.OperatingMode;
                eventCommand.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
                    JsonSerializer.Serialize(snapshot.Reasons);
                eventCommand.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value =
                    snapshot.EvaluatedAtUtc.UtcDateTime;
                await eventCommand.ExecuteNonQueryAsync(cancellationToken);
            }

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

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);

    private static void AddIdentityParameters(SqlCommand command, PortfolioRiskSnapshotV1 snapshot)
    {
        command.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.RiskSnapshotUid;
        command.Parameters.Add("@source_pnl_snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.SourcePnlSnapshotUid;
        command.Parameters.Add("@policy_uid", SqlDbType.UniqueIdentifier).Value = snapshot.PolicyUid;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = snapshot.PolicyVersion;
    }

    private static void AddSnapshotParameters(SqlCommand command, PortfolioRiskSnapshotV1 snapshot)
    {
        AddIdentityParameters(command, snapshot);
        AddControlParameters(command, snapshot);
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

    private static void AddControlParameters(SqlCommand command, PortfolioRiskSnapshotV1 snapshot)
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

public sealed class SqlServerAutomaticPortfolioRiskWorkQueue(
    SignalRiskPersistenceOptions options) : IAutomaticPortfolioRiskWorkQueue
{
    public async Task<AutomaticPortfolioRiskEnqueueResult> EnqueueAsync(
        AutomaticPortfolioRiskCandidate candidate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF EXISTS
            (
                SELECT 1 FROM [risk].[portfolio_risk_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [request_uid] = @request_uid
                   OR ([source_pnl_snapshot_uid] = @source_uid AND [policy_uid] = @policy_uid AND [policy_version] = @policy_version)
            )
                SELECT 'DUPLICATE';
            ELSE
            BEGIN
                INSERT INTO [risk].[portfolio_risk_work_items]
                (
                    [request_uid], [source_pnl_snapshot_uid], [policy_uid], [policy_version],
                    [portfolio_code], [environment], [source_as_of_utc], [current_status],
                    [next_attempt_at_utc], [reasons_json]
                )
                VALUES
                (
                    @request_uid, @source_uid, @policy_uid, @policy_version,
                    @portfolio_code, @environment, @source_as_of_utc, 'PENDING',
                    SYSUTCDATETIME(), N'[]'
                );
                SELECT 'ENQUEUED';
            END;
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            AddCandidateParameters(command, candidate);
            var outcome = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "FAILED";
            await transaction.CommitAsync(cancellationToken);
            return new AutomaticPortfolioRiskEnqueueResult(outcome, candidate.RequestUid, Array.Empty<string>());
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AutomaticPortfolioRiskEnqueueResult(AutomaticPortfolioRiskStatus.Duplicate, candidate.RequestUid, Array.Empty<string>());
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPortfolioRiskWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [risk].[portfolio_risk_work_items] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ([current_status] IN ('PENDING', 'RETRY_PENDING') AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                   OR ([current_status] = 'LEASED' AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [portfolio_risk_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED', [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[portfolio_risk_work_item_id], INSERTED.[request_uid],
                   INSERTED.[source_pnl_snapshot_uid], INSERTED.[policy_uid], INSERTED.[policy_version],
                   INSERTED.[portfolio_code], INSERTED.[environment], INSERTED.[source_as_of_utc],
                   INSERTED.[attempt_count];
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;
        var items = new List<AutomaticPortfolioRiskWorkItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AutomaticPortfolioRiskWorkItem(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)),
                reader.GetInt32(8)));
        }
        return items;
    }

    public Task CompleteAsync(long workItemId, string resultStatus, Guid riskSnapshotUid, CancellationToken cancellationToken) =>
        UpdateTerminalAsync(workItemId, resultStatus, riskSnapshotUid, null, cancellationToken);

    public Task RetryAsync(long workItemId, string error, DateTimeOffset availableAtUtc, CancellationToken cancellationToken) =>
        UpdateRetryAsync(workItemId, error, availableAtUtc, cancellationToken);

    public Task FailAsync(long workItemId, string error, CancellationToken cancellationToken) =>
        UpdateTerminalAsync(workItemId, AutomaticPortfolioRiskStatus.Failed, null, error, cancellationToken);

    private async Task UpdateRetryAsync(long id, string error, DateTimeOffset availableAtUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[portfolio_risk_work_items]
            SET [current_status] = 'RETRY_PENDING', [next_attempt_at_utc] = @available_at,
                [lease_owner] = NULL, [lease_expires_at_utc] = NULL,
                [last_error] = @error, [updated_at_utc] = SYSUTCDATETIME()
            WHERE [portfolio_risk_work_item_id] = @id AND [current_status] = 'LEASED';
            """;
        await ExecuteUpdateAsync(sql, id, null, error, availableAtUtc, cancellationToken);
    }

    private async Task UpdateTerminalAsync(long id, string status, Guid? snapshotUid, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[portfolio_risk_work_items]
            SET [current_status] = @status,
                [result_status] = CASE WHEN @status IN ('EVALUATED', 'DUPLICATE') THEN @status ELSE NULL END,
                [risk_snapshot_uid] = @snapshot_uid,
                [lease_owner] = NULL, [lease_expires_at_utc] = NULL,
                [last_error] = @error, [updated_at_utc] = SYSUTCDATETIME()
            WHERE [portfolio_risk_work_item_id] = @id AND [current_status] = 'LEASED';
            """;
        await ExecuteUpdateAsync(sql, id, status, error, null, cancellationToken, snapshotUid);
    }

    private async Task ExecuteUpdateAsync(string sql, long id, string? status, string? error, DateTimeOffset? availableAtUtc, CancellationToken cancellationToken, Guid? snapshotUid = null)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        if (sql.Contains("@status", StringComparison.Ordinal))
            command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status!;
        if (sql.Contains("@snapshot_uid", StringComparison.Ordinal))
            command.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = (object?)snapshotUid ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value = (object?)error ?? DBNull.Value;
        if (availableAtUtc is not null)
            command.Parameters.Add("@available_at", SqlDbType.DateTime2).Value = availableAtUtc.Value.UtcDateTime;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException("Portfolio risk work item is no longer leased.");
    }

    private static void AddCandidateParameters(SqlCommand command, AutomaticPortfolioRiskCandidate candidate)
    {
        command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = candidate.RequestUid;
        command.Parameters.Add("@source_uid", SqlDbType.UniqueIdentifier).Value = candidate.SourcePnlSnapshotUid;
        command.Parameters.Add("@policy_uid", SqlDbType.UniqueIdentifier).Value = candidate.PolicyUid;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value = candidate.PolicyVersion;
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = candidate.PortfolioCode;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = candidate.Environment;
        command.Parameters.Add("@source_as_of_utc", SqlDbType.DateTime2).Value = candidate.SourceAsOfUtc.UtcDateTime;
    }
}
