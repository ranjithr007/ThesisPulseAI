using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Infrastructure.Portfolio;

namespace ThesisPulse.Portfolio.Service;

public sealed class SqlServerAutomaticPaperValuationCandidateStore(
    SqlServerPortfolioLedgerOptions ledgerOptions,
    AutomaticPaperValuationOptions valuationOptions) :
    IAutomaticPaperValuationCandidateStore
{
    public async Task<IReadOnlyCollection<AutomaticPaperValuationPortfolioCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH candidate_portfolios AS
            (
                SELECT TOP (@maximum_count) p.[portfolio_id]
                FROM [portfolio].[portfolios] p WITH (READPAST)
                WHERE p.[environment] = 'PAPER'
                  AND p.[status] IN ('ACTIVE', 'RESTRICTED', 'CLOSE_ONLY')
                  AND EXISTS
                  (
                      SELECT 1
                      FROM [portfolio].[positions] pos
                      WHERE pos.[portfolio_id] = p.[portfolio_id]
                        AND pos.[status] = 'OPEN'
                        AND pos.[quantity] > 0
                  )
                ORDER BY p.[portfolio_id]
            )
            SELECT
                p.[portfolio_id], p.[portfolio_uid], p.[portfolio_code],
                p.[base_currency_code], latest.[last_snapshot_as_of_utc],
                pos.[position_id], pos.[position_uid], pos.[instrument_id],
                e.[exchange_code], i.[canonical_symbol], pos.[product_type],
                pos.[position_side], pos.[quantity], pos.[average_open_price],
                pos.[cost_basis_amount], pos.[realized_pnl_amount],
                pos.[accrued_fees_amount], pos.[accrued_taxes_amount],
                pos.[current_position_version], mapping.[broker_instrument_key]
            FROM candidate_portfolios cp
            INNER JOIN [portfolio].[portfolios] p
                ON p.[portfolio_id] = cp.[portfolio_id]
            INNER JOIN [portfolio].[positions] pos
                ON pos.[portfolio_id] = p.[portfolio_id]
               AND pos.[status] = 'OPEN'
               AND pos.[quantity] > 0
            INNER JOIN [reference].[instruments] i
                ON i.[instrument_id] = pos.[instrument_id]
            INNER JOIN [reference].[exchanges] e
                ON e.[exchange_id] = i.[exchange_id]
            OUTER APPLY
            (
                SELECT MAX(ps.[as_of_utc]) AS [last_snapshot_as_of_utc]
                FROM [portfolio].[pnl_snapshots] ps
                WHERE ps.[portfolio_id] = p.[portfolio_id]
                  AND ps.[source_version] = @policy_version
            ) latest
            OUTER APPLY
            (
                SELECT TOP (1) bim.[broker_instrument_key]
                FROM [reference].[broker_instrument_mappings] bim
                INNER JOIN [reference].[brokers] b
                    ON b.[broker_id] = bim.[broker_id]
                WHERE bim.[instrument_id] = pos.[instrument_id]
                  AND bim.[is_active] = 1
                  AND b.[broker_code] = @broker_code
                ORDER BY bim.[broker_instrument_mapping_id] DESC
            ) mapping
            ORDER BY p.[portfolio_id], pos.[position_id];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value =
            valuationOptions.ValuationPolicyVersion;
        command.Parameters.Add("@broker_code", SqlDbType.VarChar, 30).Value =
            valuationOptions.MarketDataBrokerCode;

        var portfolios = new Dictionary<long, CandidateBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var portfolioId = reader.GetInt64(0);
            if (!portfolios.TryGetValue(portfolioId, out var builder))
            {
                builder = new CandidateBuilder(
                    portfolioId,
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3).Trim(),
                    reader.IsDBNull(4) ? null : ReadUtc(reader, 4));
                portfolios.Add(portfolioId, builder);
            }

            builder.Positions.Add(new AutomaticPaperValuationPositionCandidate(
                reader.GetInt64(5),
                reader.GetGuid(6),
                reader.GetInt64(7),
                $"{reader.GetString(8)}|{reader.GetString(9)}",
                reader.GetString(10),
                reader.GetString(11),
                reader.GetDecimal(12),
                reader.GetDecimal(13),
                reader.GetDecimal(14),
                reader.GetDecimal(15),
                reader.GetDecimal(16),
                reader.GetDecimal(17),
                reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetString(19)));
        }

        return portfolios.Values
            .Select(builder => new AutomaticPaperValuationPortfolioCandidate(
                builder.PortfolioId,
                builder.PortfolioUid,
                builder.PortfolioCode,
                builder.CurrencyCode,
                builder.LastSnapshotAsOfUtc,
                builder.Positions.ToArray()))
            .ToArray();
    }

    private SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed class CandidateBuilder(
        long portfolioId,
        Guid portfolioUid,
        string portfolioCode,
        string currencyCode,
        DateTimeOffset? lastSnapshotAsOfUtc)
    {
        public long PortfolioId { get; } = portfolioId;
        public Guid PortfolioUid { get; } = portfolioUid;
        public string PortfolioCode { get; } = portfolioCode;
        public string CurrencyCode { get; } = currencyCode;
        public DateTimeOffset? LastSnapshotAsOfUtc { get; } = lastSnapshotAsOfUtc;
        public List<AutomaticPaperValuationPositionCandidate> Positions { get; } = [];
    }
}

public sealed class SqlServerAutomaticPaperValuationWorkQueue(
    SqlServerPortfolioLedgerOptions ledgerOptions) : IAutomaticPaperValuationWorkQueue
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AutomaticPaperValuationEnqueueResult> EnqueueAsync(
        AutomaticPaperValuationPayload payload,
        CancellationToken cancellationToken)
    {
        var reasons = ValidatePayload(payload);
        if (reasons.Count > 0)
        {
            return new AutomaticPaperValuationEnqueueResult(
                AutomaticPaperValuationStatus.Rejected,
                payload.RequestUid,
                reasons);
        }

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            if (!await ValidateCurrentStateAsync(
                    connection,
                    transaction,
                    payload,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new AutomaticPaperValuationEnqueueResult(
                    AutomaticPaperValuationStatus.Rejected,
                    payload.RequestUid,
                    new[] { "POSITION_VERSION_CHANGED_BEFORE_ENQUEUE" });
            }

            const string existingSql = """
                SELECT TOP (1) [current_status]
                FROM [portfolio].[valuation_work_items] WITH (UPDLOCK, HOLDLOCK)
                WHERE [request_uid] = @request_uid
                   OR [snapshot_uid] = @snapshot_uid;
                """;
            await using (var existingCommand = Command(connection, transaction, existingSql))
            {
                existingCommand.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value =
                    payload.RequestUid;
                existingCommand.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value =
                    payload.SnapshotUid;
                var existing = await existingCommand.ExecuteScalarAsync(cancellationToken);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new AutomaticPaperValuationEnqueueResult(
                        AutomaticPaperValuationStatus.Duplicate,
                        payload.RequestUid,
                        Array.Empty<string>());
                }
            }

            const string insertSql = """
                INSERT INTO [portfolio].[valuation_work_items]
                (
                    [request_uid], [snapshot_uid], [portfolio_id], [portfolio_uid],
                    [portfolio_code], [as_of_utc], [valuation_policy_version],
                    [position_fingerprint], [payload_json], [current_status],
                    [next_attempt_at_utc], [reasons_json]
                )
                VALUES
                (
                    @request_uid, @snapshot_uid, @portfolio_id, @portfolio_uid,
                    @portfolio_code, @as_of_utc, @policy_version,
                    @position_fingerprint, @payload_json, 'PENDING',
                    SYSUTCDATETIME(), N'[]'
                );
                """;
            await using var insert = Command(connection, transaction, insertSql);
            insert.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value = payload.RequestUid;
            insert.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = payload.SnapshotUid;
            insert.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = payload.Portfolio.PortfolioId;
            insert.Parameters.Add("@portfolio_uid", SqlDbType.UniqueIdentifier).Value =
                payload.Portfolio.PortfolioUid;
            insert.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value =
                payload.Portfolio.PortfolioCode;
            insert.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value =
                payload.Decision.AsOfUtc!.Value.UtcDateTime;
            insert.Parameters.Add("@policy_version", SqlDbType.VarChar, 100).Value =
                payload.PolicyVersion;
            insert.Parameters.Add("@position_fingerprint", SqlDbType.VarChar, 2000).Value =
                AutomaticPaperValuationIdentity.PositionFingerprint(payload.Portfolio);
            insert.Parameters.Add("@payload_json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(payload, JsonOptions);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AutomaticPaperValuationEnqueueResult(
                "ENQUEUED",
                payload.RequestUid,
                Array.Empty<string>());
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AutomaticPaperValuationEnqueueResult(
                AutomaticPaperValuationStatus.Duplicate,
                payload.RequestUid,
                Array.Empty<string>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AutomaticPaperValuationWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH ready AS
            (
                SELECT TOP (@maximum_count) *
                FROM [portfolio].[valuation_work_items]
                    WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE
                    ([current_status] IN ('PENDING', 'RETRY_PENDING')
                        AND [next_attempt_at_utc] <= SYSUTCDATETIME())
                    OR
                    ([current_status] = 'LEASED'
                        AND [lease_expires_at_utc] <= SYSUTCDATETIME())
                ORDER BY [next_attempt_at_utc], [valuation_work_item_id]
            )
            UPDATE ready
            SET [current_status] = 'LEASED',
                [attempt_count] = [attempt_count] + 1,
                [lease_owner] = @lease_owner,
                [lease_expires_at_utc] = DATEADD(second, @lease_seconds, SYSUTCDATETIME()),
                [updated_at_utc] = SYSUTCDATETIME()
            OUTPUT INSERTED.[valuation_work_item_id], INSERTED.[request_uid],
                   INSERTED.[snapshot_uid], INSERTED.[portfolio_id],
                   INSERTED.[portfolio_code], INSERTED.[as_of_utc],
                   INSERTED.[payload_json], INSERTED.[attempt_count];
            """;

        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = leaseOwner;
        command.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = (int)leaseDuration.TotalSeconds;
        var workItems = new List<AutomaticPaperValuationWorkItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = JsonSerializer.Deserialize<AutomaticPaperValuationPayload>(
                reader.GetString(6),
                JsonOptions)
                ?? throw new InvalidOperationException(
                    "Stored PAPER valuation payload could not be deserialized.");
            workItems.Add(new AutomaticPaperValuationWorkItem(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetString(4),
                ReadUtc(reader, 5),
                payload,
                reader.GetInt32(7)));
        }
        return workItems;
    }

    public async Task CompleteAsync(
        long workItemId,
        string resultStatus,
        Guid snapshotUid,
        CancellationToken cancellationToken)
    {
        if (resultStatus is not AutomaticPaperValuationStatus.Valued and
            not AutomaticPaperValuationStatus.Duplicate)
            throw new ArgumentOutOfRangeException(nameof(resultStatus));

        const string sql = """
            DECLARE @snapshot_id bigint;
            SELECT @snapshot_id = [pnl_snapshot_id]
            FROM [portfolio].[pnl_snapshots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [pnl_snapshot_uid] = @snapshot_uid;

            IF @snapshot_id IS NULL
                THROW 59710, 'P&L snapshot was not found for valuation completion.', 1;

            UPDATE [portfolio].[valuation_work_items]
            SET [current_status] = @result_status,
                [result_status] = @result_status,
                [pnl_snapshot_id] = @snapshot_id,
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = N'[]',
                [last_error] = NULL,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [valuation_work_item_id] = @work_item_id
              AND [current_status] IN ('LEASED', 'VALUED', 'DUPLICATE');

            IF @@ROWCOUNT = 0
                THROW 59711, 'Valuation work item was not found for completion.', 1;
            """;
        await ExecuteTransactionAsync(sql, command =>
        {
            command.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshotUid;
            command.Parameters.Add("@result_status", SqlDbType.VarChar, 30).Value = resultStatus;
            command.Parameters.Add("@work_item_id", SqlDbType.BigInt).Value = workItemId;
        }, cancellationToken);
    }

    public Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperValuationStatus.RetryPending,
            availableAtUtc,
            new[] { "PAPER_VALUATION_TRANSIENT_FAILURE" },
            error,
            cancellationToken);

    public Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            workItemId,
            AutomaticPaperValuationStatus.Rejected,
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
            AutomaticPaperValuationStatus.Failed,
            null,
            new[] { "PAPER_VALUATION_FAILED" },
            error,
            cancellationToken);

    private async Task<bool> ValidateCurrentStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperValuationPayload payload,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pos.[current_position_version], pos.[quantity], pos.[position_side],
                   p.[portfolio_uid], p.[portfolio_code]
            FROM [portfolio].[positions] pos WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [portfolio].[portfolios] p WITH (UPDLOCK, HOLDLOCK)
                ON p.[portfolio_id] = pos.[portfolio_id]
            WHERE pos.[position_id] = @position_id
              AND pos.[portfolio_id] = @portfolio_id
              AND pos.[status] = 'OPEN'
              AND pos.[quantity] > 0
              AND p.[environment] = 'PAPER';
            """;
        foreach (var position in payload.Portfolio.Positions)
        {
            await using var command = Command(connection, transaction, sql);
            command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = position.PositionId;
            command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = payload.Portfolio.PortfolioId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt32(0) != position.PositionVersion ||
                reader.GetDecimal(1) != position.Quantity ||
                !string.Equals(reader.GetString(2), position.Direction, StringComparison.Ordinal) ||
                reader.GetGuid(3) != payload.Portfolio.PortfolioUid ||
                !string.Equals(reader.GetString(4), payload.Portfolio.PortfolioCode, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static IReadOnlyCollection<string> ValidatePayload(
        AutomaticPaperValuationPayload payload)
    {
        var reasons = new List<string>();
        if (payload.RequestUid == Guid.Empty || payload.SnapshotUid == Guid.Empty)
            reasons.Add("VALUATION_IDENTITY_REQUIRED");
        if (payload.Decision.Outcome != PortfolioValuationContractV1.Valued ||
            payload.Decision.AsOfUtc is null || payload.Decision.Positions.Count == 0)
            reasons.Add("VALUATION_DECISION_NOT_READY");
        if (payload.Portfolio.Positions.Count != payload.Decision.Positions.Count)
            reasons.Add("VALUATION_POSITION_SET_MISMATCH");
        if (string.IsNullOrWhiteSpace(payload.PolicyVersion))
            reasons.Add("VALUATION_POLICY_VERSION_REQUIRED");
        return reasons;
    }

    private async Task UpdateAsync(
        long workItemId,
        string status,
        DateTimeOffset? availableAtUtc,
        IReadOnlyCollection<string> reasons,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [portfolio].[valuation_work_items]
            SET [current_status] = @status,
                [next_attempt_at_utc] = COALESCE(@next_attempt_at_utc, [next_attempt_at_utc]),
                [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL,
                [reasons_json] = @reasons_json,
                [last_error] = @last_error,
                [updated_at_utc] = SYSUTCDATETIME()
            WHERE [valuation_work_item_id] = @work_item_id;
            """;
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = Command(connection, null, sql);
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
            await using var command = Command(connection, transaction, sql);
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

    private SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

public sealed class SqlServerPaperPortfolioValuationLedgerStore(
    SqlServerPortfolioLedgerOptions ledgerOptions) : IPaperPortfolioValuationLedgerStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<PortfolioValuationPersistenceResultV1> PersistAsync(
        AutomaticPaperValuationWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var payload = workItem.Payload;
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var existingId = await FindSnapshotIdAsync(
                connection,
                transaction,
                workItem.SnapshotUid,
                workItem.PortfolioId,
                workItem.AsOfUtc,
                payload.PolicyVersion,
                cancellationToken);
            if (existingId.HasValue)
            {
                var existing = await ReadSnapshotAsync(
                    connection,
                    transaction,
                    existingId.Value,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PortfolioValuationPersistenceResultV1(
                    workItem.RequestUid,
                    PortfolioValuationContractV1.Duplicate,
                    Array.Empty<string>(),
                    existing,
                    DateTimeOffset.UtcNow);
            }

            if (!await ValidateCurrentStateAsync(
                    connection,
                    transaction,
                    payload,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new PortfolioValuationPersistenceResultV1(
                    workItem.RequestUid,
                    PortfolioValuationContractV1.Rejected,
                    new[] { "POSITION_VERSION_CHANGED_BEFORE_VALUATION" },
                    null,
                    DateTimeOffset.UtcNow);
            }

            var positionValuationIds = new Dictionary<long, long>();
            foreach (var valuation in payload.Decision.Positions)
            {
                var markId = await EnsureValuationMarkAsync(
                    connection,
                    transaction,
                    payload,
                    valuation,
                    cancellationToken);
                var valuationId = await EnsurePositionValuationAsync(
                    connection,
                    transaction,
                    valuation,
                    markId,
                    cancellationToken);
                positionValuationIds[valuation.PositionId] = valuationId;
                await UpdatePositionProjectionAsync(
                    connection,
                    transaction,
                    valuation,
                    cancellationToken);
            }

            var cashBalance = await ReadCashBalanceAsync(
                connection,
                transaction,
                payload.Portfolio.PortfolioId,
                payload.Portfolio.CurrencyCode,
                cancellationToken);
            var netLiquidationValue = Round(cashBalance + payload.Decision.NetExposureAmount);
            var previousPeak = await ReadPreviousPeakAsync(
                connection,
                transaction,
                payload.Portfolio.PortfolioId,
                workItem.AsOfUtc,
                cancellationToken);
            var drawdown = CalculateDrawdown(previousPeak, netLiquidationValue);
            var generatedAtUtc = DateTimeOffset.UtcNow < workItem.AsOfUtc
                ? workItem.AsOfUtc
                : DateTimeOffset.UtcNow;
            var positionSnapshots = payload.Decision.Positions
                .OrderBy(position => position.PositionId)
                .Select(position => new PositionPnlSnapshotV1(
                    position.PositionUid,
                    position.InstrumentKey,
                    position.ProductType,
                    position.Direction,
                    position.Quantity,
                    position.AverageOpenPrice,
                    position.MarkPrice,
                    position.MarketValueAmount,
                    position.RealizedPnlAmount,
                    position.UnrealizedPnlAmount,
                    position.AccruedFeesAmount,
                    position.AccruedTaxesAmount,
                    position.NetPnlAmount,
                    position.GrossExposureAmount,
                    position.NetExposureAmount,
                    workItem.AsOfUtc))
                .ToArray();
            var snapshot = new PortfolioPnlSnapshotV1(
                workItem.SnapshotUid,
                payload.Portfolio.PortfolioCode,
                "PAPER",
                payload.Portfolio.CurrencyCode,
                payload.Decision.RealizedPnlAmount,
                payload.Decision.UnrealizedPnlAmount,
                payload.Decision.GrossPnlAmount,
                payload.Decision.FeesAmount,
                payload.Decision.TaxesAmount,
                payload.Decision.NetPnlAmount,
                payload.Decision.GrossExposureAmount,
                payload.Decision.NetExposureAmount,
                cashBalance,
                netLiquidationValue,
                drawdown,
                drawdown,
                workItem.AsOfUtc,
                generatedAtUtc,
                positionSnapshots);
            var rawJson = JsonSerializer.Serialize(snapshot, JsonOptions);
            var snapshotHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawJson)));
            var snapshotId = await InsertSnapshotAsync(
                connection,
                transaction,
                payload,
                snapshot,
                rawJson,
                snapshotHash,
                cancellationToken);
            await InsertSnapshotPositionsAsync(
                connection,
                transaction,
                snapshotId,
                payload,
                positionValuationIds,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PortfolioValuationPersistenceResultV1(
                workItem.RequestUid,
                PortfolioValuationContractV1.Valued,
                Array.Empty<string>(),
                snapshot,
                generatedAtUtc);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await GetLatestAtAsync(
                workItem.PortfolioCode,
                workItem.AsOfUtc,
                cancellationToken);
            return new PortfolioValuationPersistenceResultV1(
                workItem.RequestUid,
                PortfolioValuationContractV1.Duplicate,
                Array.Empty<string>(),
                existing,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PortfolioPnlSnapshotV1?> GetLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioCode);
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT TOP (1) ps.[pnl_snapshot_id]
            FROM [portfolio].[pnl_snapshots] ps
            INNER JOIN [portfolio].[portfolios] p
                ON p.[portfolio_id] = ps.[portfolio_id]
            WHERE p.[portfolio_code] = @portfolio_code
              AND p.[environment] = 'PAPER'
            ORDER BY ps.[as_of_utc] DESC, ps.[pnl_snapshot_id] DESC;
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = portfolioCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? null
            : await ReadSnapshotAsync(
                connection,
                null,
                Convert.ToInt64(value),
                cancellationToken);
    }

    private async Task<PortfolioPnlSnapshotV1?> GetLatestAtAsync(
        string portfolioCode,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ledgerOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT TOP (1) ps.[pnl_snapshot_id]
            FROM [portfolio].[pnl_snapshots] ps
            INNER JOIN [portfolio].[portfolios] p
                ON p.[portfolio_id] = ps.[portfolio_id]
            WHERE p.[portfolio_code] = @portfolio_code
              AND p.[environment] = 'PAPER'
              AND ps.[as_of_utc] = @as_of_utc
            ORDER BY ps.[pnl_snapshot_id] DESC;
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@portfolio_code", SqlDbType.VarChar, 100).Value = portfolioCode;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? null
            : await ReadSnapshotAsync(connection, null, Convert.ToInt64(value), cancellationToken);
    }

    private async Task<long?> FindSnapshotIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid snapshotUid,
        long portfolioId,
        DateTimeOffset asOfUtc,
        string sourceVersion,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [pnl_snapshot_id]
            FROM [portfolio].[pnl_snapshots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [pnl_snapshot_uid] = @snapshot_uid
               OR ([portfolio_id] = @portfolio_id
                   AND [as_of_utc] = @as_of_utc
                   AND [source_version] = @source_version);
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshotUid;
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolioId;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = sourceVersion;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private async Task<bool> ValidateCurrentStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperValuationPayload payload,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [current_position_version], [quantity], [average_open_price],
                   [position_side], [status]
            FROM [portfolio].[positions] WITH (UPDLOCK, HOLDLOCK)
            WHERE [position_id] = @position_id
              AND [portfolio_id] = @portfolio_id;
            """;
        foreach (var position in payload.Portfolio.Positions)
        {
            await using var command = Command(connection, transaction, sql);
            command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = position.PositionId;
            command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = payload.Portfolio.PortfolioId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt32(0) != position.PositionVersion ||
                reader.GetDecimal(1) != position.Quantity ||
                reader.GetDecimal(2) != position.AverageOpenPrice ||
                !string.Equals(reader.GetString(3), position.Direction, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(4), "OPEN", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private async Task<long> EnsureValuationMarkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperValuationPayload payload,
        AutomaticPaperPositionValuation valuation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM [portfolio].[valuation_marks] WITH (UPDLOCK, HOLDLOCK)
                WHERE [instrument_id] = @instrument_id
                  AND [source_type] = 'MARKET_CANDLE'
                  AND [source_reference] = @source_reference
                  AND [as_of_utc] = @as_of_utc
            )
            BEGIN
                INSERT INTO [portfolio].[valuation_marks]
                (
                    [valuation_mark_uid], [instrument_id], [market_candle_id],
                    [source_type], [source_reference], [mark_price], [quality_status],
                    [is_stale], [age_milliseconds], [eligible_for_risk],
                    [as_of_utc], [received_at_utc], [created_by]
                )
                VALUES
                (
                    @mark_uid, @instrument_id, @candle_id,
                    'MARKET_CANDLE', @source_reference, @mark_price, 'VALID',
                    0, @age_milliseconds, 1,
                    @as_of_utc, @received_at_utc, @actor
                );
            END;

            SELECT [valuation_mark_id]
            FROM [portfolio].[valuation_marks]
            WHERE [instrument_id] = @instrument_id
              AND [source_type] = 'MARKET_CANDLE'
              AND [source_reference] = @source_reference
              AND [as_of_utc] = @as_of_utc;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@mark_uid", SqlDbType.UniqueIdentifier).Value =
            AutomaticPaperValuationIdentity.ValuationMarkUid(valuation);
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = valuation.InstrumentId;
        command.Parameters.Add("@candle_id", SqlDbType.BigInt).Value = valuation.Candle.CandleId;
        command.Parameters.Add("@source_reference", SqlDbType.VarChar, 200).Value =
            valuation.Candle.CandleUid.ToString("D");
        command.Parameters.Add("@mark_price", SqlDbType.Decimal).ConfigureDecimal(valuation.MarkPrice);
        command.Parameters.Add("@age_milliseconds", SqlDbType.BigInt).Value = Math.Max(
            0L,
            (long)(payload.CreatedAtUtc - valuation.Candle.CloseAtUtc).TotalMilliseconds);
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value =
            valuation.Candle.CloseAtUtc.UtcDateTime;
        command.Parameters.Add("@received_at_utc", SqlDbType.DateTime2).Value =
            (valuation.Candle.ReceivedAtUtc < valuation.Candle.CloseAtUtc
                ? valuation.Candle.CloseAtUtc
                : valuation.Candle.ReceivedAtUtc).UtcDateTime;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<long> EnsurePositionValuationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperPositionValuation valuation,
        long markId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM [portfolio].[position_valuations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [position_id] = @position_id
                  AND [valuation_mark_id] = @mark_id
                  AND [position_version] = @position_version
            )
            BEGIN
                INSERT INTO [portfolio].[position_valuations]
                (
                    [position_valuation_uid], [position_id], [valuation_mark_id],
                    [position_version], [position_side], [quantity], [average_open_price],
                    [mark_price], [cost_basis_amount], [market_value_amount],
                    [unrealized_pnl_amount], [gross_exposure_amount],
                    [net_exposure_amount], [valued_at_utc], [created_by]
                )
                VALUES
                (
                    @valuation_uid, @position_id, @mark_id,
                    @position_version, @position_side, @quantity, @average_open_price,
                    @mark_price, @cost_basis, @market_value,
                    @unrealized_pnl, @gross_exposure,
                    @net_exposure, @valued_at_utc, @actor
                );
            END;

            SELECT [position_valuation_id]
            FROM [portfolio].[position_valuations]
            WHERE [position_id] = @position_id
              AND [valuation_mark_id] = @mark_id
              AND [position_version] = @position_version;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@valuation_uid", SqlDbType.UniqueIdentifier).Value =
            AutomaticPaperValuationIdentity.PositionValuationUid(valuation);
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = valuation.PositionId;
        command.Parameters.Add("@mark_id", SqlDbType.BigInt).Value = markId;
        command.Parameters.Add("@position_version", SqlDbType.Int).Value = valuation.PositionVersion;
        command.Parameters.Add("@position_side", SqlDbType.VarChar, 10).Value = valuation.Direction;
        command.Parameters.Add("@quantity", SqlDbType.Decimal).ConfigureDecimal(valuation.Quantity);
        command.Parameters.Add("@average_open_price", SqlDbType.Decimal).ConfigureDecimal(valuation.AverageOpenPrice);
        command.Parameters.Add("@mark_price", SqlDbType.Decimal).ConfigureDecimal(valuation.MarkPrice);
        command.Parameters.Add("@cost_basis", SqlDbType.Decimal).ConfigureDecimal(valuation.CostBasisAmount);
        command.Parameters.Add("@market_value", SqlDbType.Decimal).ConfigureDecimal(valuation.MarketValueAmount);
        command.Parameters.Add("@unrealized_pnl", SqlDbType.Decimal).ConfigureDecimal(valuation.UnrealizedPnlAmount);
        command.Parameters.Add("@gross_exposure", SqlDbType.Decimal).ConfigureDecimal(valuation.GrossExposureAmount);
        command.Parameters.Add("@net_exposure", SqlDbType.Decimal).ConfigureDecimal(valuation.NetExposureAmount);
        command.Parameters.Add("@valued_at_utc", SqlDbType.DateTime2).Value = valuation.Candle.CloseAtUtc.UtcDateTime;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task UpdatePositionProjectionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperPositionValuation valuation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [portfolio].[positions]
            SET [market_value_amount] = @market_value,
                [unrealized_pnl_amount] = @unrealized_pnl,
                [last_valued_at_utc] = @valued_at_utc,
                [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = @actor
            WHERE [position_id] = @position_id
              AND [current_position_version] = @position_version
              AND [status] = 'OPEN';

            IF @@ROWCOUNT = 0
                THROW 59720, 'Position changed during valuation persistence.', 1;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@market_value", SqlDbType.Decimal).ConfigureDecimal(valuation.MarketValueAmount);
        command.Parameters.Add("@unrealized_pnl", SqlDbType.Decimal).ConfigureDecimal(valuation.UnrealizedPnlAmount);
        command.Parameters.Add("@valued_at_utc", SqlDbType.DateTime2).Value = valuation.Candle.CloseAtUtc.UtcDateTime;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
        command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = valuation.PositionId;
        command.Parameters.Add("@position_version", SqlDbType.Int).Value = valuation.PositionVersion;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<decimal> ReadCashBalanceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long portfolioId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE([total_balance_amount], 0)
            FROM [portfolio].[cash_balances] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [currency_code] = @currency_code;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolioId;
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = currencyCode;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? 0m : Convert.ToDecimal(value);
    }

    private async Task<decimal?> ReadPreviousPeakAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long portfolioId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT MAX([net_liquidation_value_amount])
            FROM [portfolio].[pnl_snapshots] WITH (UPDLOCK, HOLDLOCK)
            WHERE [portfolio_id] = @portfolio_id
              AND [as_of_utc] < @as_of_utc;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = portfolioId;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToDecimal(value);
    }

    private async Task<long> InsertSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticPaperValuationPayload payload,
        PortfolioPnlSnapshotV1 snapshot,
        string rawJson,
        string snapshotHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[pnl_snapshots]
            (
                [pnl_snapshot_uid], [portfolio_id], [currency_code],
                [realized_pnl_amount], [unrealized_pnl_amount], [gross_pnl_amount],
                [fees_amount], [taxes_amount], [net_pnl_amount],
                [gross_exposure_amount], [net_exposure_amount], [cash_balance_amount],
                [net_liquidation_value_amount], [strategy_drawdown_fraction],
                [portfolio_drawdown_fraction], [as_of_utc], [generated_at_utc],
                [source_service], [source_version], [correlation_id],
                [raw_snapshot_json], [snapshot_hash], [created_by]
            )
            VALUES
            (
                @snapshot_uid, @portfolio_id, @currency_code,
                @realized_pnl, @unrealized_pnl, @gross_pnl,
                @fees, @taxes, @net_pnl,
                @gross_exposure, @net_exposure, @cash_balance,
                @net_liquidation_value, @strategy_drawdown,
                @portfolio_drawdown, @as_of_utc, @generated_at_utc,
                @source_service, @source_version, @correlation_id,
                @raw_json, @snapshot_hash, @actor
            );
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@snapshot_uid", SqlDbType.UniqueIdentifier).Value = snapshot.PnlSnapshotUid;
        command.Parameters.Add("@portfolio_id", SqlDbType.BigInt).Value = payload.Portfolio.PortfolioId;
        command.Parameters.Add("@currency_code", SqlDbType.Char, 3).Value = snapshot.CurrencyCode;
        command.Parameters.Add("@realized_pnl", SqlDbType.Decimal).ConfigureDecimal(snapshot.RealizedPnlAmount);
        command.Parameters.Add("@unrealized_pnl", SqlDbType.Decimal).ConfigureDecimal(snapshot.UnrealizedPnlAmount);
        command.Parameters.Add("@gross_pnl", SqlDbType.Decimal).ConfigureDecimal(snapshot.GrossPnlAmount);
        command.Parameters.Add("@fees", SqlDbType.Decimal).ConfigureDecimal(snapshot.FeesAmount);
        command.Parameters.Add("@taxes", SqlDbType.Decimal).ConfigureDecimal(snapshot.TaxesAmount);
        command.Parameters.Add("@net_pnl", SqlDbType.Decimal).ConfigureDecimal(snapshot.NetPnlAmount);
        command.Parameters.Add("@gross_exposure", SqlDbType.Decimal).ConfigureDecimal(snapshot.GrossExposureAmount);
        command.Parameters.Add("@net_exposure", SqlDbType.Decimal).ConfigureDecimal(snapshot.NetExposureAmount);
        command.Parameters.Add("@cash_balance", SqlDbType.Decimal).ConfigureDecimal(snapshot.CashBalanceAmount);
        command.Parameters.Add("@net_liquidation_value", SqlDbType.Decimal).ConfigureDecimal(snapshot.NetLiquidationValueAmount);
        command.Parameters.Add("@strategy_drawdown", SqlDbType.Decimal).ConfigureFraction(snapshot.StrategyDrawdownFraction);
        command.Parameters.Add("@portfolio_drawdown", SqlDbType.Decimal).ConfigureFraction(snapshot.PortfolioDrawdownFraction);
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = snapshot.AsOfUtc.UtcDateTime;
        command.Parameters.Add("@generated_at_utc", SqlDbType.DateTime2).Value = snapshot.GeneratedAtUtc.UtcDateTime;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = "ThesisPulse.Portfolio.Service";
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = payload.PolicyVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = payload.RequestUid;
        command.Parameters.Add("@raw_json", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@snapshot_hash", SqlDbType.Char, 64).Value = snapshotHash;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task InsertSnapshotPositionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long snapshotId,
        AutomaticPaperValuationPayload payload,
        IReadOnlyDictionary<long, long> positionValuationIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [portfolio].[pnl_snapshot_positions]
            (
                [pnl_snapshot_id], [position_id], [position_valuation_id],
                [instrument_id], [realized_pnl_amount], [unrealized_pnl_amount],
                [fees_amount], [taxes_amount], [net_pnl_amount], [created_by]
            )
            VALUES
            (
                @snapshot_id, @position_id, @position_valuation_id,
                @instrument_id, @realized_pnl, @unrealized_pnl,
                @fees, @taxes, @net_pnl, @actor
            );
            """;
        foreach (var valuation in payload.Decision.Positions)
        {
            await using var command = Command(connection, transaction, sql);
            command.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
            command.Parameters.Add("@position_id", SqlDbType.BigInt).Value = valuation.PositionId;
            command.Parameters.Add("@position_valuation_id", SqlDbType.BigInt).Value =
                positionValuationIds[valuation.PositionId];
            command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = valuation.InstrumentId;
            command.Parameters.Add("@realized_pnl", SqlDbType.Decimal).ConfigureDecimal(valuation.RealizedPnlAmount);
            command.Parameters.Add("@unrealized_pnl", SqlDbType.Decimal).ConfigureDecimal(valuation.UnrealizedPnlAmount);
            command.Parameters.Add("@fees", SqlDbType.Decimal).ConfigureDecimal(valuation.AccruedFeesAmount);
            command.Parameters.Add("@taxes", SqlDbType.Decimal).ConfigureDecimal(valuation.AccruedTaxesAmount);
            command.Parameters.Add("@net_pnl", SqlDbType.Decimal).ConfigureDecimal(valuation.NetPnlAmount);
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = ledgerOptions.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<PortfolioPnlSnapshotV1> ReadSnapshotAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            SELECT ps.[pnl_snapshot_uid], p.[portfolio_code], p.[environment],
                   ps.[currency_code], ps.[realized_pnl_amount],
                   ps.[unrealized_pnl_amount], ps.[gross_pnl_amount],
                   ps.[fees_amount], ps.[taxes_amount], ps.[net_pnl_amount],
                   ps.[gross_exposure_amount], ps.[net_exposure_amount],
                   ps.[cash_balance_amount], ps.[net_liquidation_value_amount],
                   ps.[strategy_drawdown_fraction], ps.[portfolio_drawdown_fraction],
                   ps.[as_of_utc], ps.[generated_at_utc]
            FROM [portfolio].[pnl_snapshots] ps
            INNER JOIN [portfolio].[portfolios] p
                ON p.[portfolio_id] = ps.[portfolio_id]
            WHERE ps.[pnl_snapshot_id] = @snapshot_id;
            """;
        Guid snapshotUid;
        string portfolioCode;
        string environment;
        string currencyCode;
        decimal realized;
        decimal unrealized;
        decimal grossPnl;
        decimal fees;
        decimal taxes;
        decimal netPnl;
        decimal grossExposure;
        decimal netExposure;
        decimal cash;
        decimal nlv;
        decimal strategyDrawdown;
        decimal portfolioDrawdown;
        DateTimeOffset asOfUtc;
        DateTimeOffset generatedAtUtc;
        await using (var header = Command(connection, transaction, headerSql))
        {
            header.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("P&L snapshot was not found.");
            snapshotUid = reader.GetGuid(0);
            portfolioCode = reader.GetString(1);
            environment = reader.GetString(2);
            currencyCode = reader.GetString(3).Trim();
            realized = reader.GetDecimal(4);
            unrealized = reader.GetDecimal(5);
            grossPnl = reader.GetDecimal(6);
            fees = reader.GetDecimal(7);
            taxes = reader.GetDecimal(8);
            netPnl = reader.GetDecimal(9);
            grossExposure = reader.GetDecimal(10);
            netExposure = reader.GetDecimal(11);
            cash = reader.GetDecimal(12);
            nlv = reader.GetDecimal(13);
            strategyDrawdown = reader.GetDecimal(14);
            portfolioDrawdown = reader.GetDecimal(15);
            asOfUtc = ReadUtc(reader, 16);
            generatedAtUtc = ReadUtc(reader, 17);
        }

        const string positionsSql = """
            SELECT pos.[position_uid], e.[exchange_code], i.[canonical_symbol],
                   pos.[product_type], pv.[position_side], pv.[quantity],
                   pv.[average_open_price], pv.[mark_price], pv.[market_value_amount],
                   psp.[realized_pnl_amount], psp.[unrealized_pnl_amount],
                   psp.[fees_amount], psp.[taxes_amount], psp.[net_pnl_amount],
                   pv.[gross_exposure_amount], pv.[net_exposure_amount],
                   pv.[valued_at_utc]
            FROM [portfolio].[pnl_snapshot_positions] psp
            INNER JOIN [portfolio].[positions] pos
                ON pos.[position_id] = psp.[position_id]
            INNER JOIN [portfolio].[position_valuations] pv
                ON pv.[position_valuation_id] = psp.[position_valuation_id]
            INNER JOIN [reference].[instruments] i
                ON i.[instrument_id] = psp.[instrument_id]
            INNER JOIN [reference].[exchanges] e
                ON e.[exchange_id] = i.[exchange_id]
            WHERE psp.[pnl_snapshot_id] = @snapshot_id
            ORDER BY psp.[position_id];
            """;
        var positions = new List<PositionPnlSnapshotV1>();
        await using (var command = Command(connection, transaction, positionsSql))
        {
            command.Parameters.Add("@snapshot_id", SqlDbType.BigInt).Value = snapshotId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                positions.Add(new PositionPnlSnapshotV1(
                    reader.GetGuid(0),
                    $"{reader.GetString(1)}|{reader.GetString(2)}",
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetDecimal(7),
                    reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11),
                    reader.GetDecimal(12),
                    reader.GetDecimal(13),
                    reader.GetDecimal(14),
                    reader.GetDecimal(15),
                    ReadUtc(reader, 16)));
            }
        }

        return new PortfolioPnlSnapshotV1(
            snapshotUid,
            portfolioCode,
            environment,
            currencyCode,
            realized,
            unrealized,
            grossPnl,
            fees,
            taxes,
            netPnl,
            grossExposure,
            netExposure,
            cash,
            nlv,
            strategyDrawdown,
            portfolioDrawdown,
            asOfUtc,
            generatedAtUtc,
            positions);
    }

    private SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = ledgerOptions.CommandTimeoutSeconds,
        };

    private static decimal CalculateDrawdown(decimal? previousPeak, decimal current)
    {
        if (!previousPeak.HasValue || previousPeak.Value <= 0 || current >= previousPeak.Value)
            return 0m;
        return Math.Round(
            (previousPeak.Value - current) / previousPeak.Value,
            8,
            MidpointRounding.AwayFromZero);
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

internal static class SqlParameterExtensions
{
    public static SqlParameter ConfigureDecimal(this SqlParameter parameter, decimal value)
    {
        parameter.Precision = 19;
        parameter.Scale = 6;
        parameter.Value = value;
        return parameter;
    }

    public static SqlParameter ConfigureFraction(this SqlParameter parameter, decimal value)
    {
        parameter.Precision = 9;
        parameter.Scale = 8;
        parameter.Value = value;
        return parameter;
    }
}
