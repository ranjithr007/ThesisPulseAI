using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore
{
    private async Task<long> InsertReconciliationRunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        LedgerReconciliationRequestV1 request,
        PortfolioRow portfolio,
        Guid runUid,
        IReadOnlyCollection<LedgerDiscrepancyV1> discrepancies,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[reconciliation_runs]
            (
                [reconciliation_run_uid], [broker_account_id], [environment],
                [trigger_type], [scope_type], [scope_reference], [status],
                [started_at_utc], [completed_at_utc], [observation_count],
                [discrepancy_count], [unresolved_material_count],
                [source_service], [source_version], [correlation_id],
                [created_by], [updated_by]
            )
            OUTPUT INSERTED.[reconciliation_run_id]
            VALUES
            (
                @run_uid, @account_id, 'PAPER',
                @trigger, 'ACCOUNT', @scope, @status,
                @occurred_at, @occurred_at, @observations,
                @discrepancy_count, @discrepancy_count,
                @source_service, @source_version, @correlation_id,
                @actor, @actor
            );
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@run_uid", SqlDbType.UniqueIdentifier).Value = runUid;
        command.Parameters.Add("@account_id", SqlDbType.BigInt).Value = portfolio.BrokerAccountId;
        command.Parameters.Add("@trigger", SqlDbType.VarChar, 40).Value =
            request.TriggerType.Trim().ToUpperInvariant();
        command.Parameters.Add("@scope", SqlDbType.VarChar, 200).Value = request.PortfolioCode;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
            discrepancies.Count == 0 ? "SUCCEEDED" : "PARTIAL";
        AddDateTime(command, "@occurred_at", request.AsOfUtc);
        command.Parameters.Add("@observations", SqlDbType.Int).Value = discrepancies.Count + 3;
        command.Parameters.Add("@discrepancy_count", SqlDbType.Int).Value = discrepancies.Count;
        command.Parameters.Add("@source_service", SqlDbType.VarChar, 100).Value = _options.Actor;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value = _options.SourceVersion;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            SqlServerMessageValues.ToDatabaseGuid(
                request.CorrelationId,
                nameof(request.CorrelationId));
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertReconciliationDiscrepanciesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long runId,
        DateTimeOffset detectedAtUtc,
        IReadOnlyCollection<LedgerDiscrepancyV1> discrepancies,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [execution].[reconciliation_discrepancies]
            (
                [reconciliation_discrepancy_uid], [reconciliation_run_id],
                [discrepancy_type], [severity], [status], [description],
                [detected_at_utc], [blocks_new_exposure],
                [allows_risk_reducing_exits], [created_by], [updated_by]
            )
            VALUES
            (
                @uid, @run_id,
                @type, @severity, 'OPEN', @description,
                @detected_at, @blocks, @allows_exits,
                @actor, @actor
            );
            """;

        foreach (var discrepancy in discrepancies)
        {
            await using var command = Command(connection, transaction, sql);
            command.Parameters.Add("@uid", SqlDbType.UniqueIdentifier).Value =
                discrepancy.DiscrepancyUid;
            command.Parameters.Add("@run_id", SqlDbType.BigInt).Value = runId;
            command.Parameters.Add("@type", SqlDbType.VarChar, 50).Value = discrepancy.Type;
            command.Parameters.Add("@severity", SqlDbType.VarChar, 20).Value =
                discrepancy.Severity;
            command.Parameters.Add("@description", SqlDbType.NVarChar, 2000).Value =
                discrepancy.Description;
            AddDateTime(command, "@detected_at", detectedAtUtc);
            command.Parameters.Add("@blocks", SqlDbType.Bit).Value =
                discrepancy.BlocksNewExposure;
            command.Parameters.Add("@allows_exits", SqlDbType.Bit).Value =
                discrepancy.AllowsRiskReducingExits;
            command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value = _options.Actor;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
