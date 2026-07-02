using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class AuthoritativeTradePlanSignalScannerStore(
    AuthoritativeRiskSignalScannerStore inner,
    SqlServerSignalStoreOptions options) : ISignalScannerStore, ISignalDecisionProjectionStore
{
    public async Task<SignalScannerResultV1> ScanAsync(
        SignalScannerQueryV1 query,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ScanAsync(query, asOfUtc, cancellationToken);
        var projections = await LoadAsync(
            result.Signals.Select(row => row.SignalUid).ToArray(),
            cancellationToken);
        var rows = result.Signals
            .Select(row => row with
            {
                TradePlan = projections.TryGetValue(row.SignalUid, out var value)
                    ? value.TradePlan
                    : NotAvailable(),
            })
            .ToArray();
        return result with { Signals = rows, Count = rows.Length };
    }

    public async Task<SignalDecisionProjectionV1?> GetDecisionProjectionAsync(
        Guid signalUid,
        CancellationToken cancellationToken = default)
    {
        var risk = await inner.ScanAsync(
            new SignalScannerQueryV1(null, null, null, null, null, null, false, 500),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var row = risk.Signals.FirstOrDefault(item => item.SignalUid == signalUid);
        if (row is null)
            return null;

        var projections = await LoadAsync(new[] { signalUid }, cancellationToken);
        var plan = projections.TryGetValue(signalUid, out var value)
            ? value.TradePlan
            : NotAvailable();
        return new SignalDecisionProjectionV1(
            signalUid,
            row.RiskDecisionStatus,
            row.RiskDecisionUid,
            row.RiskEvaluatedAtUtc,
            plan);
    }

    private async Task<IReadOnlyDictionary<Guid, PlanProjection>> LoadAsync(
        IReadOnlyCollection<Guid> signalUids,
        CancellationToken cancellationToken)
    {
        if (signalUids.Count == 0)
            return new Dictionary<Guid, PlanProjection>();

        const string sql = """
            SELECT s.[signal_uid],
                   plan.[trade_plan_uid], plan.[approved_quantity],
                   plan.[entry_reference_price], plan.[stop_loss_price],
                   plan.[generated_at_utc], plan.[valid_until_utc],
                   COALESCE(latest_status.[status], plan.[initial_status]),
                   work.[current_status]
            FROM [intelligence].[signals] s
            INNER JOIN OPENJSON(@signal_uids_json)
                WITH ([signal_uid] uniqueidentifier '$') requested
                ON requested.[signal_uid] = s.[signal_uid]
            OUTER APPLY
            (
                SELECT TOP (1) evaluation.[signal_risk_evaluation_id]
                FROM [risk].[signal_risk_evaluations] evaluation
                WHERE evaluation.[signal_id] = s.[signal_id]
                ORDER BY evaluation.[updated_at_utc] DESC,
                         evaluation.[signal_risk_evaluation_id] DESC
            ) risk
            OUTER APPLY
            (
                SELECT TOP (1)
                    p.[trade_plan_id], p.[trade_plan_uid], p.[approved_quantity],
                    p.[entry_reference_price], p.[stop_loss_price], p.[generated_at_utc],
                    p.[valid_until_utc], p.[initial_status]
                FROM [risk].[trade_plans] p
                WHERE p.[signal_risk_evaluation_id] = risk.[signal_risk_evaluation_id]
                  AND p.[is_current] = 1
                ORDER BY p.[plan_version] DESC, p.[trade_plan_id] DESC
            ) plan
            OUTER APPLY
            (
                SELECT TOP (1) status_event.[status]
                FROM [risk].[trade_plan_status_events] status_event
                WHERE status_event.[trade_plan_id] = plan.[trade_plan_id]
                ORDER BY status_event.[event_sequence] DESC,
                         status_event.[trade_plan_status_event_id] DESC
            ) latest_status
            OUTER APPLY
            (
                SELECT TOP (1) work_item.[current_status]
                FROM [risk].[trade_plan_work_items] work_item
                WHERE work_item.[signal_risk_evaluation_id] = risk.[signal_risk_evaluation_id]
                ORDER BY work_item.[updated_at_utc] DESC,
                         work_item.[trade_plan_work_item_id] DESC
            ) work;
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@signal_uids_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(signalUids);

        var values = new Dictionary<Guid, PlanProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var planUid = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            var status = planUid.HasValue
                ? MapPlanStatus(reader.IsDBNull(7) ? null : reader.GetString(7))
                : MapWorkStatus(reader.IsDBNull(8) ? null : reader.GetString(8));
            values[reader.GetGuid(0)] = new PlanProjection(new SignalTradePlanProjectionV1(
                status,
                planUid,
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                ReadNullableUtc(reader, 5),
                ReadNullableUtc(reader, 6),
                false));
        }
        return values;
    }

    private static SignalTradePlanProjectionV1 NotAvailable() => new(
        SignalScannerContractV1.PlanNotAvailable,
        null,
        null,
        null,
        null,
        null,
        null,
        false);

    private static string MapPlanStatus(string? status) => status switch
    {
        "READY" or "PLAN_READY" => SignalScannerContractV1.PlanReady,
        "REJECTED" or "PLAN_REJECTED" => SignalScannerContractV1.PlanRejected,
        "EXPIRED" or "PLAN_EXPIRED" => SignalScannerContractV1.PlanExpired,
        "CANCELLED" or "PLAN_CANCELLED" => SignalScannerContractV1.PlanCancelled,
        "FAILED" or "PLAN_FAILED" => SignalScannerContractV1.PlanFailed,
        _ => SignalScannerContractV1.PlanNotAvailable,
    };

    private static string MapWorkStatus(string? status) => status switch
    {
        "PENDING" => SignalScannerContractV1.PlanPending,
        "LEASED" => SignalScannerContractV1.PlanBuilding,
        "RETRY_PENDING" => SignalScannerContractV1.PlanRetryPending,
        "REJECTED" => SignalScannerContractV1.PlanRejected,
        "EXPIRED" => SignalScannerContractV1.PlanExpired,
        "CANCELLED" => SignalScannerContractV1.PlanCancelled,
        "FAILED" or "COMPLETED" => SignalScannerContractV1.PlanFailed,
        _ => SignalScannerContractV1.PlanNotAvailable,
    };

    private static DateTimeOffset? ReadNullableUtc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record PlanProjection(SignalTradePlanProjectionV1 TradePlan);
}
