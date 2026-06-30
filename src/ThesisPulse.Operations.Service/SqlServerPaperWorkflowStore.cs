using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Workflows.V1;

namespace ThesisPulse.Operations.Service;

public sealed record SqlServerPaperWorkflowStoreOptions
{
    public required string ConnectionString { get; init; }

    public string Actor { get; init; } = "ThesisPulse.Operations.Service";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds));
        }
    }
}

public sealed class SqlServerPaperWorkflowStore : IPaperWorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly SqlServerPaperWorkflowStoreOptions _options;

    public SqlServerPaperWorkflowStore(SqlServerPaperWorkflowStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public async Task<StoredPaperWorkflow?> FindByIdempotencyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT TOP (1) [paper_workflow_uid]
            FROM [operations].[paper_workflows]
            WHERE [environment] = 'PAPER'
              AND [idempotency_key] = @idempotency_key;
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            idempotencyKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? null
            : await LoadAsync(connection, null, (Guid)value, false, cancellationToken);
    }

    public async Task<StoredPaperWorkflow?> GetAsync(
        Guid workflowUid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadAsync(
            connection,
            null,
            workflowUid,
            false,
            cancellationToken);
    }

    public async Task<StoredPaperWorkflow> CreateAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var existing = await FindByIdempotencyAsync(
                connection,
                transaction,
                workflow.Snapshot.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            const string sql = """
                INSERT INTO [operations].[paper_workflows]
                (
                    [paper_workflow_uid], [request_uid], [source_message_uid],
                    [environment], [idempotency_key], [correlation_id],
                    [instrument_key], [primary_timeframe], [status],
                    [current_step], [attempt_count], [request_json], [result_json],
                    [next_attempt_at_utc], [started_at_utc], [completed_at_utc],
                    [last_error_code], [last_error_message], [created_by], [updated_by]
                )
                VALUES
                (
                    @workflow_uid, @request_uid, @source_message_uid,
                    'PAPER', @idempotency_key, @correlation_id,
                    @instrument_key, @primary_timeframe, @status,
                    @current_step, @attempt_count, @request_json, @result_json,
                    @next_attempt_at_utc, @started_at_utc, @completed_at_utc,
                    @last_error_code, @last_error_message, @actor, @actor
                );
                """;
            await using var command = Command(connection, transaction, sql);
            AddWorkflowParameters(command, workflow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return workflow;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindByIdempotencyAsync(
                    workflow.Snapshot.IdempotencyKey,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "Workflow uniqueness was violated but the existing workflow could not be loaded.",
                    exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<StoredPaperWorkflow> SaveAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var locked = await LoadAsync(
                connection,
                transaction,
                workflow.Snapshot.WorkflowUid,
                true,
                cancellationToken)
                ?? throw new InvalidOperationException("Workflow does not exist.");
            if (locked.Snapshot.RequestUid != workflow.Snapshot.RequestUid)
            {
                throw new InvalidOperationException("Workflow request lineage changed.");
            }

            const string updateSql = """
                UPDATE [operations].[paper_workflows]
                SET [status] = @status,
                    [current_step] = @current_step,
                    [attempt_count] = @attempt_count,
                    [result_json] = @result_json,
                    [next_attempt_at_utc] = @next_attempt_at_utc,
                    [completed_at_utc] = @completed_at_utc,
                    [last_error_code] = @last_error_code,
                    [last_error_message] = @last_error_message,
                    [updated_at_utc] = @updated_at_utc,
                    [updated_by] = @actor
                WHERE [paper_workflow_uid] = @workflow_uid;
                """;
            await using (var command = Command(connection, transaction, updateSql))
            {
                AddWorkflowParameters(command, workflow);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("Workflow update did not affect one row.");
                }
            }

            foreach (var step in workflow.Steps.Values)
            {
                await UpsertStepAsync(
                    connection,
                    transaction,
                    workflow.Snapshot.WorkflowUid,
                    step,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return workflow;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<Guid>> GetDueWorkflowUidsAsync(
        DateTimeOffset asOfUtc,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT TOP (@maximum_count) [paper_workflow_uid]
            FROM [operations].[paper_workflows]
            WHERE [status] = 'RETRY_PENDING'
              AND [next_attempt_at_utc] <= @as_of_utc
            ORDER BY [next_attempt_at_utc], [paper_workflow_id];
            """;
        await using var command = Command(connection, null, sql);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        AddTime(command, "@as_of_utc", asOfUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private async Task<StoredPaperWorkflow?> FindByIdempotencyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [paper_workflow_uid]
            FROM [operations].[paper_workflows] WITH (UPDLOCK, HOLDLOCK)
            WHERE [environment] = 'PAPER'
              AND [idempotency_key] = @idempotency_key;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            idempotencyKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? null
            : await LoadAsync(
                connection,
                transaction,
                (Guid)value,
                true,
                cancellationToken);
    }

    private async Task<StoredPaperWorkflow?> LoadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid workflowUid,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT
                [paper_workflow_id], [paper_workflow_uid], [request_uid],
                [source_message_uid], [environment], [idempotency_key],
                [correlation_id], [instrument_key], [primary_timeframe],
                [status], [current_step], [attempt_count], [request_json],
                [result_json], [next_attempt_at_utc], [started_at_utc],
                [completed_at_utc], [last_error_code], [last_error_message],
                [updated_at_utc]
            FROM [operations].[paper_workflows]{hint}
            WHERE [paper_workflow_uid] = @workflow_uid;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@workflow_uid", SqlDbType.UniqueIdentifier).Value =
            workflowUid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var workflowId = reader.GetInt64(0);
        var requestJson = reader.GetString(12);
        var request = JsonSerializer.Deserialize<PaperWorkflowStartRequestV1>(
            requestJson,
            JsonOptions)
            ?? throw new InvalidOperationException("Stored workflow request is invalid.");
        var snapshot = new PaperWorkflowSnapshotV1(
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(5),
            reader.GetGuid(6).ToString("D"),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            Utc(reader, 15),
            Utc(reader, 19),
            reader.IsDBNull(14) ? null : Utc(reader, 14),
            reader.IsDBNull(16) ? null : Utc(reader, 16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            Array.Empty<PaperWorkflowStepSnapshotV1>());
        var resultJson = reader.IsDBNull(13) ? null : reader.GetString(13);
        await reader.DisposeAsync();

        var steps = await LoadStepsAsync(
            connection,
            transaction,
            workflowId,
            lockForUpdate,
            cancellationToken);
        return new StoredPaperWorkflow(
            snapshot with
            {
                Steps = steps.Values
                    .OrderBy(step => step.Snapshot.Sequence)
                    .Select(step => step.Snapshot)
                    .ToArray(),
            },
            request,
            resultJson,
            steps);
    }

    private async Task<IReadOnlyDictionary<string, StoredPaperWorkflowStep>> LoadStepsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long workflowId,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var hint = lockForUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        var sql = $"""
            SELECT
                [paper_workflow_step_uid], [step_code], [step_sequence],
                [status], [attempt_count], [request_json], [response_json],
                [output_reference], [retryable], [started_at_utc],
                [completed_at_utc], [error_code], [error_message]
            FROM [operations].[paper_workflow_steps]{hint}
            WHERE [paper_workflow_id] = @workflow_id
            ORDER BY [step_sequence];
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@workflow_id", SqlDbType.BigInt).Value = workflowId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var steps = new Dictionary<string, StoredPaperWorkflowStep>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = new PaperWorkflowStepSnapshotV1(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : Utc(reader, 9),
                reader.IsDBNull(10) ? null : Utc(reader, 10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12));
            steps[snapshot.StepCode] = new StoredPaperWorkflowStep(
                snapshot,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        return steps;
    }

    private async Task UpsertStepAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid workflowUid,
        StoredPaperWorkflowStep step,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @workflow_id bigint;
            SELECT @workflow_id = [paper_workflow_id]
            FROM [operations].[paper_workflows] WITH (UPDLOCK, HOLDLOCK)
            WHERE [paper_workflow_uid] = @workflow_uid;

            IF EXISTS
            (
                SELECT 1
                FROM [operations].[paper_workflow_steps] WITH (UPDLOCK, HOLDLOCK)
                WHERE [paper_workflow_id] = @workflow_id
                  AND [step_code] = @step_code
            )
            BEGIN
                UPDATE [operations].[paper_workflow_steps]
                SET [status] = @status,
                    [attempt_count] = @attempt_count,
                    [request_json] = @request_json,
                    [response_json] = @response_json,
                    [output_reference] = @output_reference,
                    [retryable] = @retryable,
                    [started_at_utc] = @started_at_utc,
                    [completed_at_utc] = @completed_at_utc,
                    [error_code] = @error_code,
                    [error_message] = @error_message,
                    [updated_at_utc] = @updated_at_utc,
                    [updated_by] = @actor
                WHERE [paper_workflow_id] = @workflow_id
                  AND [step_code] = @step_code;
            END
            ELSE
            BEGIN
                INSERT INTO [operations].[paper_workflow_steps]
                (
                    [paper_workflow_step_uid], [paper_workflow_id], [step_code],
                    [step_sequence], [status], [attempt_count], [request_json],
                    [response_json], [output_reference], [retryable],
                    [started_at_utc], [completed_at_utc], [error_code],
                    [error_message], [created_by], [updated_by]
                )
                VALUES
                (
                    @step_uid, @workflow_id, @step_code,
                    @step_sequence, @status, @attempt_count, @request_json,
                    @response_json, @output_reference, @retryable,
                    @started_at_utc, @completed_at_utc, @error_code,
                    @error_message, @actor, @actor
                );
            END;
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add("@workflow_uid", SqlDbType.UniqueIdentifier).Value =
            workflowUid;
        command.Parameters.Add("@step_uid", SqlDbType.UniqueIdentifier).Value =
            step.Snapshot.StepUid;
        command.Parameters.Add("@step_code", SqlDbType.VarChar, 60).Value =
            step.Snapshot.StepCode;
        command.Parameters.Add("@step_sequence", SqlDbType.Int).Value =
            step.Snapshot.Sequence;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
            step.Snapshot.Status;
        command.Parameters.Add("@attempt_count", SqlDbType.Int).Value =
            step.Snapshot.AttemptCount;
        AddNullableString(command, "@request_json", step.RequestJson, SqlDbType.NVarChar, -1);
        AddNullableString(command, "@response_json", step.ResponseJson, SqlDbType.NVarChar, -1);
        AddNullableString(
            command,
            "@output_reference",
            step.Snapshot.OutputReference,
            SqlDbType.VarChar,
            300);
        command.Parameters.Add("@retryable", SqlDbType.Bit).Value =
            step.Snapshot.Retryable;
        AddNullableTime(command, "@started_at_utc", step.Snapshot.StartedAtUtc);
        AddNullableTime(command, "@completed_at_utc", step.Snapshot.CompletedAtUtc);
        AddNullableString(
            command,
            "@error_code",
            step.Snapshot.ErrorCode,
            SqlDbType.VarChar,
            100);
        AddNullableString(
            command,
            "@error_message",
            step.Snapshot.ErrorMessage,
            SqlDbType.NVarChar,
            2000);
        AddTime(command, "@updated_at_utc", DateTimeOffset.UtcNow);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddWorkflowParameters(
        SqlCommand command,
        StoredPaperWorkflow workflow)
    {
        var snapshot = workflow.Snapshot;
        command.Parameters.Add("@workflow_uid", SqlDbType.UniqueIdentifier).Value =
            snapshot.WorkflowUid;
        command.Parameters.Add("@request_uid", SqlDbType.UniqueIdentifier).Value =
            snapshot.RequestUid;
        command.Parameters.Add("@source_message_uid", SqlDbType.UniqueIdentifier).Value =
            snapshot.SourceMessageUid;
        command.Parameters.Add("@idempotency_key", SqlDbType.VarChar, 200).Value =
            snapshot.IdempotencyKey;
        command.Parameters.Add("@correlation_id", SqlDbType.UniqueIdentifier).Value =
            Guid.Parse(snapshot.CorrelationId);
        command.Parameters.Add("@instrument_key", SqlDbType.VarChar, 200).Value =
            snapshot.InstrumentKey;
        command.Parameters.Add("@primary_timeframe", SqlDbType.VarChar, 20).Value =
            snapshot.PrimaryTimeframe;
        command.Parameters.Add("@status", SqlDbType.VarChar, 30).Value = snapshot.Status;
        AddNullableString(command, "@current_step", snapshot.CurrentStep, SqlDbType.VarChar, 60);
        command.Parameters.Add("@attempt_count", SqlDbType.Int).Value = snapshot.AttemptCount;
        command.Parameters.Add("@request_json", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(workflow.Request, JsonOptions);
        AddNullableString(command, "@result_json", workflow.ResultJson, SqlDbType.NVarChar, -1);
        AddNullableTime(command, "@next_attempt_at_utc", snapshot.NextAttemptAtUtc);
        AddTime(command, "@started_at_utc", snapshot.StartedAtUtc);
        AddNullableTime(command, "@completed_at_utc", snapshot.CompletedAtUtc);
        AddNullableString(
            command,
            "@last_error_code",
            snapshot.LastErrorCode,
            SqlDbType.VarChar,
            100);
        AddNullableString(
            command,
            "@last_error_message",
            snapshot.LastErrorMessage,
            SqlDbType.NVarChar,
            2000);
        AddTime(command, "@updated_at_utc", snapshot.UpdatedAtUtc);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 256).Value =
            _options.Actor;
    }

    private SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

    private static void AddNullableString(
        SqlCommand command,
        string name,
        string? value,
        SqlDbType type,
        int size) =>
        command.Parameters.Add(name, type, size).Value =
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void AddTime(
        SqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value.UtcDateTime;

    private static void AddNullableTime(
        SqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value =
            value is null ? DBNull.Value : value.Value.UtcDateTime;

    private static DateTimeOffset Utc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
