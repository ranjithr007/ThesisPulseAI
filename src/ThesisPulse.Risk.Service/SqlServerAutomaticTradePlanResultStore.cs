using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed record AutomaticTradePlanPersistenceResult(
    string Outcome,
    Guid TradePlanUid,
    long TradePlanId);

public interface IAutomaticTradePlanResultStore
{
    Task<AutomaticTradePlanPersistenceResult> PersistReadyAsync(
        AutomaticTradePlanCommandV1 command,
        TradePlanBuildResultV1 result,
        CancellationToken cancellationToken);
}

public sealed class SqlServerAutomaticTradePlanResultStore(
    SignalRiskPersistenceOptions persistenceOptions) : IAutomaticTradePlanResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AutomaticTradePlanPersistenceResult> PersistReadyAsync(
        AutomaticTradePlanCommandV1 command,
        TradePlanBuildResultV1 result,
        CancellationToken cancellationToken)
    {
        if (result.Status != TradePlanContractV1.Ready || result.TradePlan is null)
            throw new InvalidOperationException("Only READY Trade Plans can be persisted.");
        if (result.TradePlan.ExecutionAuthorized)
            throw new InvalidOperationException("A persisted Trade Plan cannot authorize execution.");
        if (!Guid.TryParse(command.CorrelationId, out var correlationId))
            throw new InvalidOperationException("Trade Plan correlation ID must be a GUID.");

        ValidateResultLineage(command, result);

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var lineage = await ResolveLineageAsync(
                connection,
                transaction,
                command,
                correlationId,
                cancellationToken);
            var existing = await FindExistingAsync(
                connection,
                transaction,
                lineage.SignalRiskEvaluationId,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new AutomaticTradePlanPersistenceResult(
                    "DUPLICATE",
                    existing.Value.TradePlanUid,
                    existing.Value.TradePlanId);
            }

            var plan = result.TradePlan;
            var rawJson = JsonSerializer.Serialize(plan, JsonOptions);
            var contractHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
            var metadataJson = JsonSerializer.Serialize(new
            {
                command.CommandUid,
                command.RequestUid,
                command.RiskDecisionUid,
                candidateThesisUid = lineage.CandidateThesisUid,
                thesisLineageSource = "intelligence.signal_fusion_lineage",
                result.BuilderVersion,
                result.ExecutionPolicyVersion,
                plan.RiskAmountAtStop,
                plan.CapitalAtReference,
                plan.FirstTargetRiskReward,
                executionAuthorized = false,
            }, JsonOptions);
            var tradePlanId = await InsertPlanAsync(
                connection,
                transaction,
                lineage,
                command,
                result,
                correlationId,
                rawJson,
                metadataJson,
                contractHash,
                cancellationToken);
            await InsertTargetsAsync(
                connection,
                transaction,
                tradePlanId,
                plan.Targets,
                cancellationToken);
            await InsertStatusEventAsync(
                connection,
                transaction,
                tradePlanId,
                command,
                correlationId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AutomaticTradePlanPersistenceResult(
                "CREATED",
                plan.TradePlanUid,
                tradePlanId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void ValidateResultLineage(
        AutomaticTradePlanCommandV1 command,
        TradePlanBuildResultV1 result)
    {
        var plan = result.TradePlan!;
        if (plan.RiskDecisionUid != command.RiskDecisionUid)
            throw new InvalidOperationException("READY Trade Plan risk-decision UID does not match its command.");
        if (plan.SignalUid != command.SignalUid)
            throw new InvalidOperationException("READY Trade Plan signal UID does not match its command.");
        if (plan.ThesisUid != command.ThesisUid)
            throw new InvalidOperationException("READY Trade Plan candidate-thesis UID does not match its command.");
        if (!string.Equals(
                plan.CorrelationId,
                command.CorrelationId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CorrelationId,
                command.CorrelationId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "READY Trade Plan correlation ID does not match its command.");
        }
    }

    private static async Task<TradePlanLineage> ResolveLineageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutomaticTradePlanCommandV1 command,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.[signal_risk_evaluation_id], e.[signal_id], s.[instrument_id],
                   lineage.[thesis_uid]
            FROM [risk].[signal_risk_evaluations] e WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [intelligence].[signals] s
                ON s.[signal_id] = e.[signal_id]
            INNER JOIN [intelligence].[signal_fusion_lineage] lineage
                ON lineage.[signal_id] = s.[signal_id]
            WHERE e.[risk_decision_uid] = @risk_decision_uid
              AND e.[current_status] = 'RISK_APPROVED'
              AND s.[signal_uid] = @signal_uid
              AND lineage.[thesis_uid] = @thesis_uid
              AND e.[correlation_id] = @correlation_id
              AND s.[correlation_id] = @correlation_id;
            """;
        await using var sqlCommand = new SqlCommand(sql, connection, transaction);
        sqlCommand.Parameters.Add("@risk_decision_uid", SqlDbType.UniqueIdentifier).Value =
            command.RiskDecisionUid;
        sqlCommand.Parameters.Add("@signal_uid", SqlDbType.UniqueIdentifier).Value =
            command.SignalUid;
        sqlCommand.Parameters.Add("@thesis_uid", SqlDbType.UniqueIdentifier).Value =
            command.ThesisUid;
        sqlCommand.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            correlationId;
        await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Approved authoritative Risk, Signal and candidate-thesis lineage was not found.");
        }

        return new TradePlanLineage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetGuid(3));
    }

    private static async Task<(long TradePlanId, Guid TradePlanUid)?> FindExistingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long signalRiskEvaluationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [trade_plan_id], [trade_plan_uid]
            FROM [risk].[trade_plans] WITH (UPDLOCK, HOLDLOCK)
            WHERE [signal_risk_evaluation_id] = @evaluation_id
              AND [is_current] = 1;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@evaluation_id", SqlDbType.BigInt).Value = signalRiskEvaluationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetGuid(1));
    }

    private static async Task<long> InsertPlanAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TradePlanLineage lineage,
        AutomaticTradePlanCommandV1 source,
        TradePlanBuildResultV1 result,
        Guid correlationId,
        string rawJson,
        string metadataJson,
        string contractHash,
        CancellationToken cancellationToken)
    {
        var plan = result.TradePlan!;
        const string sql = """
            INSERT INTO [risk].[trade_plans]
            (
                [trade_plan_uid], [message_uid], [risk_decision_id], [signal_risk_evaluation_id],
                [thesis_id], [candidate_thesis_uid], [signal_id], [instrument_id],
                [contract_version], [environment], [source_service], [source_version],
                [plan_version], [side], [position_intent], [entry_order_type],
                [entry_reference_price], [entry_limit_price], [entry_trigger_price],
                [minimum_acceptable_price], [maximum_acceptable_price], [approved_quantity],
                [minimum_execution_quantity], [allow_partial_fill], [stop_loss_price],
                [stop_loss_order_type], [stop_loss_limit_price], [stop_loss_is_mandatory],
                [maximum_slippage_fraction], [time_in_force], [trade_date], [not_before_utc],
                [new_entry_cutoff_utc], [mandatory_exit_by_utc], [allow_trailing_stop],
                [allow_break_even_move], [allow_time_exit], [allow_signal_exit],
                [exit_policy_version], [execution_policy_version], [initial_status],
                [status_reasons_json], [generated_at_utc], [valid_until_utc],
                [supersedes_trade_plan_uid], [is_current], [correlation_id], [causation_id],
                [metadata_json], [raw_contract_json], [contract_hash], [created_by]
            )
            OUTPUT INSERTED.[trade_plan_id]
            VALUES
            (
                @trade_plan_uid, @message_uid, NULL, @evaluation_id,
                NULL, @candidate_thesis_uid, @signal_id, @instrument_id,
                @contract_version, @environment, 'ThesisPulse.Risk.Service', @source_version,
                1, @side, @position_intent, @entry_order_type,
                @entry_reference_price, @entry_limit_price, @entry_trigger_price,
                @minimum_acceptable_price, @maximum_acceptable_price, @approved_quantity,
                @minimum_execution_quantity, @allow_partial_fill, @stop_loss_price,
                @stop_loss_order_type, @stop_loss_limit_price, 1,
                @maximum_slippage_fraction, @time_in_force, @trade_date, @not_before_utc,
                @new_entry_cutoff_utc, @mandatory_exit_by_utc, @allow_trailing_stop,
                @allow_break_even_move, @allow_time_exit, @allow_signal_exit,
                @exit_policy_version, @execution_policy_version, 'READY', N'[]',
                @generated_at_utc, @valid_until_utc, NULL, 1, @correlation_id, @causation_id,
                @metadata_json, @raw_contract_json, @contract_hash, N'ThesisPulse.Risk.Service'
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@trade_plan_uid", SqlDbType.UniqueIdentifier).Value =
            plan.TradePlanUid;
        command.Parameters.Add("@message_uid", SqlDbType.UniqueIdentifier).Value =
            source.SourceMessageUid;
        command.Parameters.Add("@evaluation_id", SqlDbType.BigInt).Value =
            lineage.SignalRiskEvaluationId;
        command.Parameters.Add("@candidate_thesis_uid", SqlDbType.UniqueIdentifier).Value =
            lineage.CandidateThesisUid;
        command.Parameters.Add("@signal_id", SqlDbType.BigInt).Value = lineage.SignalId;
        command.Parameters.Add("@instrument_id", SqlDbType.BigInt).Value = lineage.InstrumentId;
        command.Parameters.Add("@contract_version", SqlDbType.VarChar, 20).Value =
            TradePlanContractV1.ContractVersion;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 20).Value = plan.Environment;
        command.Parameters.Add("@source_version", SqlDbType.VarChar, 50).Value =
            result.BuilderVersion;
        command.Parameters.Add("@side", SqlDbType.VarChar, 10).Value = plan.Side;
        command.Parameters.Add("@position_intent", SqlDbType.VarChar, 20).Value =
            plan.PositionIntent;
        command.Parameters.Add("@entry_order_type", SqlDbType.VarChar, 20).Value =
            plan.Entry.OrderType;
        AddDecimal(command, "@entry_reference_price", plan.Entry.ReferencePrice, 19, 6);
        AddNullableDecimal(command, "@entry_limit_price", plan.Entry.LimitPrice, 19, 6);
        AddNullableDecimal(command, "@entry_trigger_price", plan.Entry.TriggerPrice, 19, 6);
        AddDecimal(command, "@minimum_acceptable_price", plan.Entry.MinimumAcceptablePrice, 19, 6);
        AddDecimal(command, "@maximum_acceptable_price", plan.Entry.MaximumAcceptablePrice, 19, 6);
        AddDecimal(command, "@approved_quantity", plan.ApprovedQuantity, 19, 6);
        AddNullableDecimal(
            command,
            "@minimum_execution_quantity",
            plan.MinimumExecutionQuantity,
            19,
            6);
        command.Parameters.Add("@allow_partial_fill", SqlDbType.Bit).Value = plan.AllowPartialFill;
        AddDecimal(command, "@stop_loss_price", plan.StopLoss.Price, 19, 6);
        command.Parameters.Add("@stop_loss_order_type", SqlDbType.VarChar, 20).Value =
            plan.StopLoss.OrderType;
        AddNullableDecimal(command, "@stop_loss_limit_price", plan.StopLoss.LimitPrice, 19, 6);
        AddDecimal(command, "@maximum_slippage_fraction", plan.MaximumSlippageFraction, 9, 8);
        command.Parameters.Add("@time_in_force", SqlDbType.VarChar, 10).Value = plan.TimeInForce;
        command.Parameters.Add("@trade_date", SqlDbType.Date).Value =
            plan.Session.TradeDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@not_before_utc", SqlDbType.DateTime2).Value =
            plan.Session.NotBeforeUtc?.UtcDateTime ?? (object)DBNull.Value;
        command.Parameters.Add("@new_entry_cutoff_utc", SqlDbType.DateTime2).Value =
            plan.Session.NewEntryCutoffUtc.UtcDateTime;
        command.Parameters.Add("@mandatory_exit_by_utc", SqlDbType.DateTime2).Value =
            plan.Session.MandatoryExitByUtc.UtcDateTime;
        command.Parameters.Add("@allow_trailing_stop", SqlDbType.Bit).Value =
            plan.ExitPolicy.AllowTrailingStop;
        command.Parameters.Add("@allow_break_even_move", SqlDbType.Bit).Value =
            plan.ExitPolicy.AllowBreakEvenMove;
        command.Parameters.Add("@allow_time_exit", SqlDbType.Bit).Value =
            plan.ExitPolicy.AllowTimeExit;
        command.Parameters.Add("@allow_signal_exit", SqlDbType.Bit).Value =
            plan.ExitPolicy.AllowSignalExit;
        command.Parameters.Add("@exit_policy_version", SqlDbType.VarChar, 50).Value =
            plan.ExitPolicy.PolicyVersion;
        command.Parameters.Add("@execution_policy_version", SqlDbType.VarChar, 50).Value =
            plan.ExecutionPolicyVersion;
        command.Parameters.Add("@generated_at_utc", SqlDbType.DateTime2).Value =
            plan.GeneratedAtUtc.UtcDateTime;
        command.Parameters.Add("@valid_until_utc", SqlDbType.DateTime2).Value =
            plan.ValidUntilUtc.UtcDateTime;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = correlationId;
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            source.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value = metadataJson;
        command.Parameters.Add("@raw_contract_json", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@contract_hash", SqlDbType.Char, 64).Value = contractHash;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertTargetsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long tradePlanId,
        IReadOnlyCollection<TradePlanTargetV1> targets,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[trade_plan_targets]
                ([trade_plan_id], [target_sequence], [target_price], [quantity_fraction], [created_by])
            VALUES
                (@trade_plan_id, @target_sequence, @target_price, @quantity_fraction, N'ThesisPulse.Risk.Service');
            """;
        foreach (var target in targets.OrderBy(target => target.Sequence))
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = tradePlanId;
            command.Parameters.Add("@target_sequence", SqlDbType.Int).Value = target.Sequence;
            AddDecimal(command, "@target_price", target.Price, 19, 6);
            AddDecimal(command, "@quantity_fraction", target.QuantityFraction, 9, 8);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertStatusEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long tradePlanId,
        AutomaticTradePlanCommandV1 source,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO [risk].[trade_plan_status_events]
            (
                [trade_plan_id], [event_sequence], [status], [reason_codes_json],
                [occurred_at_utc], [source_service], [source_version], [correlation_id],
                [causation_id], [metadata_json], [created_by]
            )
            VALUES
            (
                @trade_plan_id, 0, 'READY', N'[]',
                @occurred_at_utc, 'ThesisPulse.Risk.Service', 'phase-3.3', @correlation_id,
                @causation_id, @metadata_json, N'ThesisPulse.Risk.Service'
            );
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@trade_plan_id", SqlDbType.BigInt).Value = tradePlanId;
        command.Parameters.Add("@occurred_at_utc", SqlDbType.DateTime2).Value =
            source.CreatedAtUtc.UtcDateTime;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value = correlationId;
        command.Parameters.Add("@causation_id", SqlDbType.UniqueIdentifier).Value =
            source.CausationMessageUid is Guid causation ? causation : DBNull.Value;
        command.Parameters.Add("@metadata_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(new { source.CommandUid, source.RequestUid }, JsonOptions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddDecimal(
        SqlCommand command,
        string name,
        decimal value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string name,
        decimal? value,
        byte precision,
        byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private sealed record TradePlanLineage(
        long SignalRiskEvaluationId,
        long SignalId,
        long InstrumentId,
        Guid CandidateThesisUid);
}
