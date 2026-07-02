using System.Data;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.Risk.Service;

public sealed record AutomaticTradePlanWorkerSnapshot(
    long Leased,
    long Completed,
    long Duplicates,
    long Rejected,
    long Expired,
    long Retried,
    long Failed,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc);

public sealed class AutomaticTradePlanWorkerState
{
    private long _leased;
    private long _completed;
    private long _duplicates;
    private long _rejected;
    private long _expired;
    private long _retried;
    private long _failed;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;

    public void Leased(int count) => Interlocked.Add(ref _leased, count);
    public void Completed(bool duplicate)
    {
        Interlocked.Increment(ref _completed);
        if (duplicate) Interlocked.Increment(ref _duplicates);
        _lastSuccessUtc = DateTimeOffset.UtcNow;
    }
    public void Rejected()
    {
        Interlocked.Increment(ref _rejected);
        _lastSuccessUtc = DateTimeOffset.UtcNow;
    }
    public void Expired()
    {
        Interlocked.Increment(ref _expired);
        _lastSuccessUtc = DateTimeOffset.UtcNow;
    }
    public void Retried()
    {
        Interlocked.Increment(ref _retried);
        _lastFailureUtc = DateTimeOffset.UtcNow;
    }
    public void Failed()
    {
        Interlocked.Increment(ref _failed);
        _lastFailureUtc = DateTimeOffset.UtcNow;
    }

    public AutomaticTradePlanWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _completed),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _failed),
        _lastSuccessUtc,
        _lastFailureUtc);
}

public sealed record AutomaticTradePlanMetricsSnapshot(
    long Pending,
    long Building,
    long Ready,
    long Rejected,
    long RetryPending,
    long Expired,
    long Cancelled,
    long Failed,
    long Completed,
    AutomaticTradePlanWorkerSnapshot Worker,
    DateTimeOffset ObservedAtUtc);

public interface IAutomaticTradePlanMetricsStore
{
    Task<AutomaticTradePlanMetricsSnapshot> ReadAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryAutomaticTradePlanMetricsStore(
    AutomaticTradePlanWorkerState state) : IAutomaticTradePlanMetricsStore
{
    public Task<AutomaticTradePlanMetricsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var worker = state.Snapshot();
        return Task.FromResult(new AutomaticTradePlanMetricsSnapshot(
            0,
            0,
            worker.Completed - worker.Duplicates,
            worker.Rejected,
            worker.Retried,
            worker.Expired,
            0,
            worker.Failed,
            worker.Completed,
            worker,
            DateTimeOffset.UtcNow));
    }
}

public sealed class SqlServerAutomaticTradePlanMetricsStore(
    SignalRiskPersistenceOptions persistenceOptions,
    AutomaticTradePlanWorkerState state) : IAutomaticTradePlanMetricsStore
{
    public async Task<AutomaticTradePlanMetricsSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(SUM(CASE WHEN [current_status] = 'PENDING' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'LEASED' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'REJECTED' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'RETRY_PENDING' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'EXPIRED' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'CANCELLED' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'FAILED' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN [current_status] = 'COMPLETED' THEN 1 ELSE 0 END), 0)
            FROM [risk].[trade_plan_work_items];

            SELECT COUNT_BIG(*)
            FROM [risk].[trade_plans]
            WHERE [signal_risk_evaluation_id] IS NOT NULL
              AND [is_current] = 1
              AND [initial_status] = 'READY';
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Trade Plan queue metrics query returned no row.");

        var pending = Convert.ToInt64(reader.GetValue(0));
        var building = Convert.ToInt64(reader.GetValue(1));
        var rejected = Convert.ToInt64(reader.GetValue(2));
        var retryPending = Convert.ToInt64(reader.GetValue(3));
        var expired = Convert.ToInt64(reader.GetValue(4));
        var cancelled = Convert.ToInt64(reader.GetValue(5));
        var failed = Convert.ToInt64(reader.GetValue(6));
        var completed = Convert.ToInt64(reader.GetValue(7));
        if (!await reader.NextResultAsync(cancellationToken) || !await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Trade Plan persistence metrics query returned no row.");
        var ready = reader.GetInt64(0);

        return new AutomaticTradePlanMetricsSnapshot(
            pending,
            building,
            ready,
            rejected,
            retryPending,
            expired,
            cancelled,
            failed,
            completed,
            state.Snapshot(),
            DateTimeOffset.UtcNow);
    }
}
