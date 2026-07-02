using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

/// <summary>
/// Keeps the shared SQL execution ledger authoritative while enriching the public PAPER order
/// snapshot with the deterministic internal gateway order reference persisted by Phase 3.5.
/// </summary>
public sealed class SqlServerPaperExecutionLedgerDecorator(
    SqlServerPaperExecutionLedgerOptions options) : IPaperExecutionLedgerStore
{
    private readonly SqlServerPaperExecutionLedgerStore _inner = new(options);

    public Task<ExecutionCommandResultV1?> FindAuthorizationAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        _inner.FindAuthorizationAsync(idempotencyKey, cancellationToken);

    public Task<ExecutionCommandResultV1> PersistAuthorizationAsync(
        ExecutionCommandResultV1 result,
        CancellationToken cancellationToken = default) =>
        _inner.PersistAuthorizationAsync(result, cancellationToken);

    public async Task<PaperOrderSnapshotV1?> GetOrderAsync(
        Guid paperOrderUid,
        CancellationToken cancellationToken = default)
    {
        var order = await _inner.GetOrderAsync(paperOrderUid, cancellationToken);
        if (order is null)
            return null;

        var brokerOrderId = await ReadBrokerOrderIdAsync(
            paperOrderUid,
            cancellationToken);
        return string.IsNullOrWhiteSpace(brokerOrderId)
            ? order
            : order with { BrokerOrderId = brokerOrderId };
    }

    public async Task<PaperOrderTransitionResultV1> ApplyEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        Func<PaperOrderSnapshotV1, PaperOrderTransitionResultV1> transition,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ApplyEventAsync(
            paperOrderUid,
            request,
            transition,
            cancellationToken);
        if (result.PaperOrder is null)
            return result;

        var brokerOrderId = await ReadBrokerOrderIdAsync(
            paperOrderUid,
            cancellationToken);
        return string.IsNullOrWhiteSpace(brokerOrderId)
            ? result
            : result with
            {
                PaperOrder = result.PaperOrder with { BrokerOrderId = brokerOrderId },
            };
    }

    private async Task<string?> ReadBrokerOrderIdAsync(
        Guid paperOrderUid,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [broker_order_id]
            FROM [execution].[orders]
            WHERE [order_uid] = @order_uid
              AND [environment] = 'PAPER';
            """;
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@order_uid", SqlDbType.UniqueIdentifier).Value =
            paperOrderUid;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
