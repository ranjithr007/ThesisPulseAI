using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Risk.Service;

public sealed record SignalRiskMetricsSnapshot(
    long Pending,
    long Evaluating,
    long Approved,
    long Rejected,
    long Restricted,
    long RetryPending,
    long Expired,
    long Failed,
    long Completed,
    long Duplicates,
    DateTimeOffset ObservedAtUtc);

public interface ISignalRiskMetricsStore
{
    Task<SignalRiskMetricsSnapshot> ReadAsync(CancellationToken cancellationToken);
}

public sealed class InMemorySignalRiskMetricsStore(
    SignalRiskWorkerState workerState) : ISignalRiskMetricsStore
{
    public Task<SignalRiskMetricsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = workerState.Read();
        return Task.FromResult(new SignalRiskMetricsSnapshot(
            0,
            0,
            0,
            0,
            0,
            snapshot.Retried,
            snapshot.Expired,
            snapshot.Failed,
            snapshot.Completed,
            snapshot.Duplicates,
            DateTimeOffset.UtcNow));
    }
}

public sealed class SqlServerSignalRiskMetricsStore(
    SignalRiskPersistenceOptions options,
    SignalRiskWorkerState workerState) : ISignalRiskMetricsStore
{
    public async Task<SignalRiskMetricsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN w.[status] IN ('PENDING','RETRY_PENDING') THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_EVALUATING' THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_APPROVED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_REJECTED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_RESTRICTED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_RETRY_PENDING' THEN 1 ELSE 0 END),
                SUM(CASE WHEN e.[current_status] = 'RISK_EXPIRED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN w.[status] = 'FAILED' THEN 1 ELSE 0 END),
                SUM(CASE WHEN w.[status] = 'COMPLETED' THEN 1 ELSE 0 END)
            FROM [risk].[signal_risk_work_items] w
            LEFT JOIN [risk].[signal_risk_evaluations] e
                ON e.[source_message_uid] = w.[message_uid];
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var worker = workerState.Read();

        long Value(int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

        return new SignalRiskMetricsSnapshot(
            Value(0),
            Value(1),
            Value(2),
            Value(3),
            Value(4),
            Value(5),
            Value(6),
            Value(7),
            Value(8),
            worker.Duplicates,
            DateTimeOffset.UtcNow);
    }
}
