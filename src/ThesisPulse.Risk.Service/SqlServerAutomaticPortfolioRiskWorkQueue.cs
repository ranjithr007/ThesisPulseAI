using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Risk.Service;

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
                SELECT 1
                FROM [risk].[portfolio_risk_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [request_uid] = @request_uid
                   OR
                   (
                       [source_pnl_snapshot_uid] = @source_uid
                       AND [policy_uid] = @policy_uid
                       AND [policy_version] = @policy_version
                   )
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            AddCandidateParameters(command, candidate);
            var outcome = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))
                ?? AutomaticPortfolioRiskStatus.Failed;
            await transaction.CommitAsync(cancellationToken);
            return new AutomaticPortfolioRiskEnqueueResult(
                outcome,
                candidate.RequestUid,
                Array.Empty<string>());
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AutomaticPortfolioRiskEnqueueResult(
                AutomaticPortfolioRiskStatus.Duplicate,
                candidate.RequestUid,
                Array.Empty<string>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPortfolioRiskWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (maximumCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [risk].[portfolio_risk_work_items] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING', 'RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [portfolio_risk_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT
                INSERTED.[portfolio_risk_work_item_id],
                INSERTED.[request_uid],
                INSERTED.[source_pnl_snapshot_uid],
                INSERTED.[policy_uid],
                INSERTED.[policy_version],
                INSERTED.[portfolio_code],
                INSERTED.[environment],
                INSERTED.[source_as_of_utc],
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
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)),
                reader.GetInt32(8)));
        }

        return items;
    }

    public Task CompleteAsync(
        long workItemId,
        string resultStatus,
        Guid riskSnapshotUid,
        CancellationToken cancellationToken)
    {
        if (resultStatus is not AutomaticPortfolioRiskStatus.Evaluated and
            not AutomaticPortfolioRiskStatus.Duplicate)
            throw new ArgumentOutOfRangeException(nameof(resultStatus));

        return UpdateTerminalAsync(
            workItemId,
            resultStatus,
            riskSnapshotUid,
            null,
            cancellationToken);
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateRetryAsync(workItemId, error, availableAtUtc, cancellationToken);

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateTerminalAsync(
            workItemId,
            AutomaticPortfolioRiskStatus.Failed,
            null,
            error,
            cancellationToken);

    private async Task UpdateRetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[portfolio_risk_work_items]
            SET [current_status] = 'RETRY_PENDING',
                [next_attempt_at_utc] = @available_at_utc,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error] = @error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [portfolio_risk_work_item_id] = @work_item_id
              AND [current_status] = 'LEASED';
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        command.Parameters.Add("@available_at_utc", SqlDbType.DateTime2).Value = availableAtUtc.UtcDateTime;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value = error;
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    private async Task UpdateTerminalAsync(
        long workItemId,
        string status,
        Guid? riskSnapshotUid,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [risk].[portfolio_risk_work_items]
            SET [current_status] = @status,
                [result_status] =
                    CASE WHEN @status IN ('EVALUATED', 'DUPLICATE') THEN @status ELSE NULL END,
                [risk_snapshot_uid] = @risk_snapshot_uid,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [last_error] = @error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [portfolio_risk_work_item_id] = @work_item_id
              AND [current_status] = 'LEASED';
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@risk_snapshot_uid", SqlDbType.UniqueIdentifier).Value =
            (object?)riskSnapshotUid ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 2000).Value =
            (object?)error ?? DBNull.Value;
        await EnsureSingleUpdateAsync(command, cancellationToken);
    }

    private static async Task EnsureSingleUpdateAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException(
                "Portfolio risk work item is no longer leased.");
    }

    private static void AddCandidateParameters(
        SqlCommand command,
        AutomaticPortfolioRiskCandidate candidate)
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
