using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class AuthoritativeRiskSignalScannerStore(
    SqlServerSignalStore inner,
    SqlServerSignalStoreOptions options) : ISignalScannerStore
{
    public async Task<SignalScannerResultV1> ScanAsync(
        SignalScannerQueryV1 query,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ScanAsync(query, asOfUtc, cancellationToken);
        if (result.Signals.Count == 0)
            return result;

        var riskBySignal = await LoadLatestRiskAsync(
            result.Signals.Select(row => row.SignalUid).ToArray(),
            cancellationToken);
        var rows = result.Signals
            .Select(row => riskBySignal.TryGetValue(row.SignalUid, out var risk)
                ? row with
                {
                    RiskDecisionStatus = MapRiskStatus(risk.Status),
                    RiskDecisionUid = risk.DecisionUid,
                    RiskEvaluatedAtUtc = risk.EvaluatedAtUtc,
                }
                : row)
            .ToArray();

        return result with { Signals = rows, Count = rows.Length };
    }

    private async Task<IReadOnlyDictionary<Guid, RiskProjection>> LoadLatestRiskAsync(
        IReadOnlyCollection<Guid> signalUids,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.[signal_uid], latest.[current_status], latest.[risk_decision_uid], latest.[updated_at_utc]
            FROM [intelligence].[signals] s
            INNER JOIN OPENJSON(@signal_uids_json)
                WITH ([signal_uid] uniqueidentifier '$') requested
                ON requested.[signal_uid] = s.[signal_uid]
            OUTER APPLY
            (
                SELECT TOP (1)
                    evaluation.[current_status],
                    evaluation.[risk_decision_uid],
                    evaluation.[updated_at_utc]
                FROM [risk].[signal_risk_evaluations] evaluation
                WHERE evaluation.[signal_id] = s.[signal_id]
                ORDER BY evaluation.[updated_at_utc] DESC,
                         evaluation.[signal_risk_evaluation_id] DESC
            ) latest
            WHERE latest.[current_status] IS NOT NULL;
            """;

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        command.Parameters.Add("@signal_uids_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(signalUids);

        var values = new Dictionary<Guid, RiskProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetGuid(0)] = new RiskProjection(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
        }

        return values;
    }

    private static string MapRiskStatus(string status) => status switch
    {
        "RISK_APPROVED" => SignalScannerContractV1.RiskApproved,
        "RISK_REJECTED" => SignalScannerContractV1.RiskRejected,
        "RISK_RESTRICTED" => SignalScannerContractV1.RiskRestricted,
        _ => SignalScannerContractV1.RiskNotEvaluated,
    };

    private sealed record RiskProjection(
        string Status,
        Guid? DecisionUid,
        DateTimeOffset EvaluatedAtUtc);
}
