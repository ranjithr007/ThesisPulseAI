using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Infrastructure.Portfolio;

namespace ThesisPulse.Portfolio.Service;

public sealed class SqlServerAutomaticPortfolioFillProjectionCandidateStore(
    SqlServerPortfolioLedgerOptions ledgerOptions) :
    IAutomaticPortfolioFillProjectionCandidateStore
{
    public async Task<IReadOnlyCollection<AutomaticPortfolioFillProjectionCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                f.[fill_id],
                f.[fill_uid],
                p.[portfolio_id],
                p.[portfolio_code],
                f.[correlation_id],
                f.[fill_at_utc],
                s.[strategy_code],
                p.[status],
                p.[effective_from_utc],
                p.[effective_to_utc]
            FROM [execution].[fills] f WITH (READPAST)
            INNER JOIN [execution].[orders] o
                ON o.[order_id] = f.[order_id]
            INNER JOIN [risk].[trade_plans] tp
                ON tp.[trade_plan_id] = f.[trade_plan_id]
            LEFT JOIN [intelligence].[signals] s
                ON s.[signal_id] = tp.[signal_id]
            LEFT JOIN [portfolio].[portfolios] p
                ON p.[broker_account_id] = f.[broker_account_id]
               AND p.[environment] = f.[environment]
               AND p.[strategy_code] = s.[strategy_code]
            WHERE f.[environment] = 'PAPER'
              AND f.[fill_quantity] > 0
              AND f.[fill_price] > 0
              AND o.[environment] = 'PAPER'
              AND o.[current_status] IN ('PARTIALLY_FILLED', 'FILLED')
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [portfolio].[fill_projection_work_items] w
                  WHERE w.[fill_id] = f.[fill_id]
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [portfolio].[position_events] pe
                  WHERE pe.[fill_id] = f.[fill_id]
              )
            ORDER BY f.[fill_at_utc], f.[fill_id];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;

        var candidates = new List<AutomaticPortfolioFillProjectionCandidate>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fillAtUtc = ReadUtc(reader, 5);
            var reasons = new List<string>();
            if (reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6)))
                reasons.Add("SIGNAL_STRATEGY_NOT_FOUND");
            if (reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                reasons.Add("PAPER_PORTFOLIO_ROUTING_NOT_FOUND");
            }
            else
            {
                var status = reader.GetString(7);
                if (status is not "ACTIVE" and not "RESTRICTED" and not "CLOSE_ONLY")
                    reasons.Add("PAPER_PORTFOLIO_NOT_POSTABLE");
                var effectiveFromUtc = ReadUtc(reader, 8);
                var effectiveToUtc = reader.IsDBNull(9) ? null : ReadUtc(reader, 9);
                if (fillAtUtc < effectiveFromUtc ||
                    (effectiveToUtc.HasValue && fillAtUtc >= effectiveToUtc.Value))
                    reasons.Add("PAPER_PORTFOLIO_EFFECTIVE_WINDOW_INVALID");
            }

            candidates.Add(new AutomaticPortfolioFillProjectionCandidate(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetGuid(4).ToString("D"),
                fillAtUtc,
                reasons.Distinct(StringComparer.Ordinal).ToArray()));
        }

        return candidates;
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

public sealed class SqlServerAutomaticPortfolioFillProjectionWorkQueue(
    SqlServerPortfolioLedgerOptions ledgerOptions,
    AutomaticPortfolioFillProjectionOptions projectionOptions) :
    IAutomaticPortfolioFillProjectionWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AutomaticPortfolioFillProjectionEnqueueResult> EnqueueAsync(
        AutomaticPortfolioFillProjectionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reasons = AutomaticPortfolioFillProjectionCandidateValidator.Validate(candidate);
        var rejected = reasons.Count > 0;
        var requestUid = AutomaticPortfolioFillProjectionIdentity.RequestUid(
            candidate.FillUid,
            projectionOptions.ProjectionPolicyVersion);

        const string sql = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM [execution].[fills] WITH (UPDLOCK, HOLDLOCK)
                WHERE [fill_id] = @fill_id
                  AND [fill_uid] = @fill_uid
                  AND [environment] = 'PAPER'
                  AND [fill_quantity] > 0
                  AND [fill_price] > 0
            )
            BEGIN
                SELECT CAST(-1 AS int);
                RETURN;
            END;

            IF EXISTS
            (
                SELECT 1
                FROM [portfolio].[fill_projection_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [fill_id] = @fill_id OR [fill_uid] = @fill_uid
            )
            BEGIN
                SELECT CAST(0 AS int);
                RETURN;
            END;

            INSERT INTO [portfolio].[fill_projection_work_items]
            (
                [fill_id], [fill_uid], [portfolio_id], [portfolio_code],
                [projection_request_uid], [correlation_id], [fill_at_utc],
                [projection_policy_version], [current_status], [next_attempt_at_utc],
                [reasons_json]
            )
            VALUES
            (
                @fill_id, @fill_uid, @portfolio_id, @portfolio_code,
                @projection_request_uid, @correlation_id, @fill_at_utc,
                @projection_policy_version, @current_status, SYSUTCDATETIME(),
                @reasons_json
            );
            SELECT CAST(1 AS int);
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.Add("@fill_id", SqlDbType.BigInt).Value = candidate.FillId;
            command.Parameters.Add("@fill_uid", SqlDbType.UniqueIdentifier).Value = candidate.FillUid;
            command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value =
                candidate.PortfolioId.HasValue ? candidate.PortfolioId.Value : DBNull.Value;
            command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
                string.IsNullOrWhiteSpace(candidate.PortfolioCode)
                    ? DBNull.Value
                    : candidate.PortfolioCode;
            command.Parameters.Add("@projection_request_uid", SqlDbType.UniqueIdentifier).Value = requestUid;
            command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
                Guid.TryParse(candidate.CorrelationId, out var correlationUid)
                    ? correlationUid
                    : Guid.Empty;
            command.Parameters.Add("@fill_at_utc", SqlDbType.DateTime2).Value = candidate.FillAtUtc.UtcDateTime;
            command.Parameters.Add("@projection_policy_version", SqlDbType.VarChar, 100).Value =
                projectionOptions.ProjectionPolicyVersion;
            command.Parameters.Add("@current_status", SqlDbType.VarChar, 30).Value =
                rejected
                    ? AutomaticPortfolioFillProjectionStatus.Rejected
                    : AutomaticPortfolioFillProjectionStatus.Pending;
            command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(reasons, JsonOptions);

            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            if (outcome < 0)
            {
                return new AutomaticPortfolioFillProjectionEnqueueResult(
                    AutomaticPortfolioFillProjectionStatus.Rejected,
                    candidate.FillUid,
                    new[] { "AUTHORITATIVE_PAPER_FILL_NOT_FOUND" });
            }

            if (outcome == 0)
            {
                return new AutomaticPortfolioFillProjectionEnqueueResult(
                    "DUPLICATE",
                    candidate.FillUid,
                    Array.Empty<string>());
            }

            return new AutomaticPortfolioFillProjectionEnqueueResult(
                rejected
                    ? AutomaticPortfolioFillProjectionStatus.Rejected
                    : "ENQUEUED",
                candidate.FillUid,
                reasons);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPortfolioFillProjectionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [portfolio].[fill_projection_work_items]
                    WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING', 'RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [fill_projection_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[fill_projection_work_item_id], INSERTED.[fill_id],
                   INSERTED.[fill_uid], INSERTED.[portfolio_id], INSERTED.[portfolio_code],
                   INSERTED.[projection_request_uid], INSERTED.[correlation_id],
                   INSERTED.[fill_at_utc], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;

        var items = new List<AutomaticPortfolioFillProjectionWorkItem>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AutomaticPortfolioFillProjectionWorkItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetGuid(5),
                reader.GetGuid(6).ToString("D"),
                ReadUtc(reader, 7),
                reader.GetInt32(8)));
        }

        return items;
    }

    public async Task CompleteAsync(
        long workItemId,
        string projectionStatus,
        Guid? positionUid,
        CancellationToken cancellationToken)
    {
        if (projectionStatus is not AutomaticPortfolioFillProjectionStatus.Projected and
            not AutomaticPortfolioFillProjectionStatus.Duplicate)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionStatus));
        }

        const string sql = """
            DECLARE @fill_id bigint;
            DECLARE @portfolio_id bigint;
            SELECT @fill_id = [fill_id], @portfolio_id = [portfolio_id]
            FROM [portfolio].[fill_projection_work_items] WITH (UPDLOCK, HOLDLOCK)
            WHERE [fill_projection_work_item_id] = @work_item_id
              AND [current_status] IN ('LEASED', 'PROJECTED', 'DUPLICATE');

            IF @fill_id IS NULL
                THROW 59610, 'Portfolio fill projection work item was not found.', 1;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [portfolio].[position_events] pe WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN [portfolio].[positions] p
                    ON p.[position_id] = pe.[position_id]
                WHERE pe.[fill_id] = @fill_id
                  AND p.[portfolio_id] = @portfolio_id
                  AND (@position_uid IS NULL OR p.[position_uid] = @position_uid)
            )
                THROW 59611, 'Authoritative portfolio position event was not found for fill.', 1;

            UPDATE [portfolio].[fill_projection_work_items]
            SET [current_status] = @projection_status,
                [projection_result_status] = @projection_status,
                [position_uid] = @position_uid,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = N'[]',
                [last_error] = NULL,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [fill_projection_work_item_id] = @work_item_id;
            """;

        await ExecuteTransactionAsync(sql, command =>
        {
            command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
            command.Parameters.Add("@projection_status", SqlDbType.VarChar, 30).Value = projectionStatus;
            command.Parameters.Add("@position_uid", SqlDbType.UniqueIdentifier).Value =
                positionUid.HasValue ? positionUid.Value : DBNull.Value;
        }, cancellationToken);
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPortfolioFillProjectionStatus.RetryPending,
            availableAtUtc,
            new[] { "PORTFOLIO_FILL_PROJECTION_TRANSIENT_FAILURE" },
            error,
            cancellationToken);

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPortfolioFillProjectionStatus.Rejected,
            null,
            reasons,
            null,
            cancellationToken);

    public Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPortfolioFillProjectionStatus.Failed,
            null,
            new[] { "PORTFOLIO_FILL_PROJECTION_FAILED" },
            error,
            cancellationToken);

    private async Task UpdateAsync(
        long workItemId,
        string status,
        DateTimeOffset? availableAtUtc,
        IReadOnlyCollection<string> reasons,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [portfolio].[fill_projection_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = @reasons_json,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [fill_projection_work_item_id] = @work_item_id;
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql);
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = status;
        command.Parameters.Add("@next_attempt_at_utc", SqlDbType.DateTime2).Value =
            availableAtUtc.HasValue ? availableAtUtc.Value.UtcDateTime : DBNull.Value;
        command.Parameters.Add("@reasons_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(reasons, JsonOptions);
        command.Parameters.Add("@last_error", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : error;
        command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteTransactionAsync(
        string sql,
        Action<SqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, transaction, sql);
            configure(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private SqlCommand CreateCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
