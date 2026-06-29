using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace ThesisPulse.DatabaseMigrator;

public sealed class DatabaseMigrator
{
    private const string LockResourcePrefix = "ThesisPulseAI.DatabaseMigrator";

    private readonly MigratorOptions _options;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public DatabaseMigrator(
        MigratorOptions options,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    public async Task<MigrationSummary> RunAsync(CancellationToken cancellationToken)
    {
        var scripts = MigrationScript.Discover(_options.MigrationsPath);

        await using var connection = new SqlConnection(_options.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        connection.InfoMessage += (_, eventArgs) =>
        {
            foreach (SqlError message in eventArgs.Errors)
            {
                _output.WriteLine($"SQL {message.Number}: {message.Message}");
            }
        };

        await connection.OpenAsync(cancellationToken);
        _output.WriteLine($"Connected to server '{connection.DataSource}', database '{connection.Database}'.");
        _output.WriteLine($"Environment: {_options.Environment}");
        _output.WriteLine($"Migration directory: {_options.MigrationsPath}");

        var lockResource = $"{LockResourcePrefix}:{connection.Database}";
        await AcquireLockAsync(connection, lockResource, cancellationToken);
        _output.WriteLine("Acquired exclusive database migration lock.");

        try
        {
            var ledgerState = await ReadLedgerStateAsync(connection, cancellationToken);
            var applied = ledgerState.IsInitialized
                ? await LoadAppliedMigrationsAsync(connection, cancellationToken)
                : Array.Empty<AppliedMigration>();

            ValidateAppliedHistory(scripts, applied);

            var appliedBySequence = applied.ToDictionary(item => item.Sequence);
            var pending = scripts
                .Where(script => !appliedBySequence.ContainsKey(script.Sequence))
                .ToArray();

            foreach (var script in scripts)
            {
                var status = appliedBySequence.ContainsKey(script.Sequence) ? "APPLIED" : "PENDING";
                _output.WriteLine($"[{status}] {script.Name}  sha256:{script.Checksum}");
            }

            if (_options.DryRun)
            {
                _output.WriteLine($"Dry run complete. {pending.Length} migration(s) would be applied.");
                return new MigrationSummary(scripts.Length, applied.Length, pending.Length, 0, true);
            }

            if (pending.Length == 0)
            {
                _output.WriteLine("Database is current. No migrations were executed.");
                return new MigrationSummary(scripts.Length, applied.Length, 0, 0, false);
            }

            var appliedCount = 0;
            foreach (var script in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyMigrationAsync(connection, script, cancellationToken);
                appliedCount++;
            }

            _output.WriteLine($"Migration completed successfully. Applied {appliedCount} migration(s).");
            return new MigrationSummary(
                scripts.Length,
                applied.Length,
                pending.Length,
                appliedCount,
                false);
        }
        finally
        {
            await ReleaseLockAsync(connection, lockResource, cancellationToken);
        }
    }

    private async Task ApplyMigrationAsync(
        SqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        _output.WriteLine($"Applying {script.Name}...");
        var startedAtUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        long? runId = null;

        var ledgerState = await ReadLedgerStateAsync(connection, cancellationToken);
        if (ledgerState.IsInitialized)
        {
            runId = await InsertStartedRunAsync(
                connection,
                ledgerState.DatabaseIdentity!.Value,
                script,
                startedAtUtc,
                cancellationToken);
        }

        try
        {
            var batches = SqlBatchSplitter.Split(script.Content);
            if (batches.Count == 0)
            {
                throw new MigrationValidationException(
                    $"Migration {script.Name} contains no executable SQL batches.");
            }

            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                command.CommandText = batch.Text;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = _options.CommandTimeoutSeconds;

                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is SqlException or InvalidOperationException)
                {
                    throw new MigrationExecutionException(
                        script,
                        batch.StartLine,
                        batch.RepeatIndex,
                        exception);
                }
            }

            stopwatch.Stop();
            var completedAtUtc = DateTime.UtcNow;
            var finalLedgerState = await ReadLedgerStateAsync(connection, cancellationToken);
            if (!finalLedgerState.IsInitialized || finalLedgerState.DatabaseIdentity is null)
            {
                throw new MigrationExecutionException(
                    script,
                    1,
                    1,
                    new InvalidOperationException(
                        "Migration metadata tables are unavailable after script execution."));
            }

            await RecordSuccessAsync(
                connection,
                finalLedgerState.DatabaseIdentity.Value,
                script,
                runId,
                startedAtUtc,
                completedAtUtc,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);

            _output.WriteLine(
                $"Applied {script.Name} in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            await RollBackOpenTransactionAsync(connection, cancellationToken);

            try
            {
                var currentLedgerState = await ReadLedgerStateAsync(connection, cancellationToken);
                if (currentLedgerState.IsInitialized && currentLedgerState.DatabaseIdentity is not null)
                {
                    await RecordFailureAsync(
                        connection,
                        currentLedgerState.DatabaseIdentity.Value,
                        script,
                        runId,
                        startedAtUtc,
                        DateTime.UtcNow,
                        stopwatch.ElapsedMilliseconds,
                        exception,
                        cancellationToken);
                }
            }
            catch (Exception recordingException)
            {
                await _error.WriteLineAsync(
                    $"WARNING: Could not record the failed migration attempt: {recordingException.Message}");
            }

            if (exception is MigrationExecutionException)
            {
                throw;
            }

            throw new MigrationExecutionException(script, 1, 1, exception);
        }
    }

    private static void ValidateAppliedHistory(
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyList<AppliedMigration> applied)
    {
        if (applied.Count == 0)
        {
            return;
        }

        var scriptsBySequence = scripts.ToDictionary(script => script.Sequence);
        var orderedApplied = applied.OrderBy(item => item.Sequence).ToArray();

        for (var index = 0; index < orderedApplied.Length; index++)
        {
            var appliedMigration = orderedApplied[index];
            var expectedSequence = index + 1;
            if (appliedMigration.Sequence != expectedSequence)
            {
                throw new MigrationValidationException(
                    $"Applied migration history is not contiguous. Expected V{expectedSequence:D4} but found V{appliedMigration.Sequence:D4}.");
            }

            if (!scriptsBySequence.TryGetValue(appliedMigration.Sequence, out var localScript))
            {
                throw new MigrationValidationException(
                    $"Database contains applied migration V{appliedMigration.Sequence:D4}, but the script is missing locally.");
            }

            if (!string.Equals(
                    localScript.Name,
                    appliedMigration.Name,
                    StringComparison.Ordinal))
            {
                throw new MigrationValidationException(
                    $"Migration name mismatch for V{appliedMigration.Sequence:D4}. " +
                    $"Database: '{appliedMigration.Name}', repository: '{localScript.Name}'.");
            }

            if (!string.Equals(
                    localScript.Checksum,
                    appliedMigration.Checksum,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationValidationException(
                    $"Checksum mismatch for already-applied migration {localScript.Name}. " +
                    $"Database: {appliedMigration.Checksum}, repository: {localScript.Checksum}. " +
                    "Applied migrations are immutable; create a new migration instead of editing history.");
            }
        }
    }

    private async Task AcquireLockAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _options.LockTimeoutSeconds + 5;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = @lock_timeout_ms,
                @DbPrincipal = 'public';
            SELECT @result;
            """;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
        command.Parameters.Add("@lock_timeout_ms", SqlDbType.Int).Value =
            checked(_options.LockTimeoutSeconds * 1000);

        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        if (result < 0)
        {
            throw new DatabaseLockException(
                $"Could not acquire the migration lock within {_options.LockTimeoutSeconds} seconds. SQL lock result: {result}.");
        }
    }

    private async Task ReleaseLockAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Min(_options.CommandTimeoutSeconds, 30);
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_releaseapplock
                    @Resource = @resource,
                    @LockOwner = 'Session',
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            await command.ExecuteScalarAsync(cancellationToken);
            _output.WriteLine("Released database migration lock.");
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            await _error.WriteLineAsync(
                $"WARNING: Could not explicitly release the migration lock: {exception.Message}");
        }
    }

    private static async Task<LedgerState> ReadLedgerStateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CASE WHEN OBJECT_ID(N'[operations].[database_metadata]', N'U') IS NULL THEN 0 ELSE 1 END,
                CASE WHEN OBJECT_ID(N'[operations].[schema_migrations]', N'U') IS NULL THEN 0 ELSE 1 END,
                CASE WHEN OBJECT_ID(N'[operations].[migration_runs]', N'U') IS NULL THEN 0 ELSE 1 END;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new MigrationValidationException("Could not inspect migration ledger state.");
        }

        var metadataExists = reader.GetInt32(0) == 1;
        var migrationsExist = reader.GetInt32(1) == 1;
        var runsExist = reader.GetInt32(2) == 1;
        await reader.CloseAsync();

        if (!metadataExists && !migrationsExist && !runsExist)
        {
            return new LedgerState(false, null);
        }

        if (!metadataExists || !migrationsExist || !runsExist)
        {
            throw new MigrationValidationException(
                "Migration metadata is partially initialized. operations.database_metadata, " +
                "operations.schema_migrations and operations.migration_runs must either all exist or all be absent.");
        }

        await using var identityCommand = connection.CreateCommand();
        identityCommand.CommandText = """
            SELECT [database_identity]
            FROM [operations].[database_metadata]
            WHERE [database_metadata_id] = 1;
            """;
        var identityResult = await identityCommand.ExecuteScalarAsync(cancellationToken);
        if (identityResult is not Guid databaseIdentity)
        {
            throw new MigrationValidationException(
                "operations.database_metadata does not contain the required singleton database identity.");
        }

        return new LedgerState(true, databaseIdentity);
    }

    private static async Task<AppliedMigration[]> LoadAppliedMigrationsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                [migration_sequence],
                [migration_name],
                RTRIM([script_checksum])
            FROM [operations].[schema_migrations]
            ORDER BY [migration_sequence];
            """;

        var results = new List<AppliedMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return results.ToArray();
    }

    private async Task<long> InsertStartedRunAsync(
        SqlConnection connection,
        Guid databaseIdentity,
        MigrationScript script,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [operations].[migration_runs]
            (
                [migration_sequence],
                [migration_name],
                [script_checksum],
                [database_identity],
                [environment],
                [application_version],
                [started_at_utc],
                [outcome],
                [executed_by]
            )
            OUTPUT INSERTED.[migration_run_id]
            VALUES
            (
                @sequence,
                @name,
                @checksum,
                @database_identity,
                @environment,
                @application_version,
                @started_at_utc,
                'STARTED',
                COALESCE(SUSER_SNAME(), N'UNKNOWN')
            );
            """;
        AddMigrationParameters(command, databaseIdentity, script, startedAtUtc);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RecordSuccessAsync(
        SqlConnection connection,
        Guid databaseIdentity,
        MigrationScript script,
        long? runId,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMs,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = """
                    INSERT INTO [operations].[schema_migrations]
                    (
                        [migration_sequence],
                        [migration_name],
                        [script_checksum],
                        [database_identity],
                        [environment],
                        [application_version],
                        [applied_at_utc],
                        [duration_ms],
                        [applied_by]
                    )
                    VALUES
                    (
                        @sequence,
                        @name,
                        @checksum,
                        @database_identity,
                        @environment,
                        @application_version,
                        @completed_at_utc,
                        @duration_ms,
                        COALESCE(SUSER_SNAME(), N'UNKNOWN')
                    );
                    """;
                AddMigrationParameters(migrationCommand, databaseIdentity, script, startedAtUtc);
                migrationCommand.Parameters.Add("@completed_at_utc", SqlDbType.DateTime2).Value = completedAtUtc;
                migrationCommand.Parameters.Add("@duration_ms", SqlDbType.BigInt).Value = durationMs;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (runId is null)
            {
                await InsertCompletedRunAsync(
                    connection,
                    transaction,
                    databaseIdentity,
                    script,
                    startedAtUtc,
                    completedAtUtc,
                    durationMs,
                    "SUCCEEDED",
                    null,
                    null,
                    cancellationToken);
            }
            else
            {
                await UpdateRunAsync(
                    connection,
                    transaction,
                    runId.Value,
                    completedAtUtc,
                    durationMs,
                    "SUCCEEDED",
                    null,
                    null,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task RecordFailureAsync(
        SqlConnection connection,
        Guid databaseIdentity,
        MigrationScript script,
        long? runId,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMs,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var rootException = exception is MigrationExecutionException migrationException
            ? migrationException.InnerException ?? migrationException
            : exception;
        var errorNumber = rootException is SqlException sqlException
            ? sqlException.Number
            : (int?)null;
        var errorMessage = Truncate(rootException.Message, 4000);

        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (runId is null)
            {
                await InsertCompletedRunAsync(
                    connection,
                    transaction,
                    databaseIdentity,
                    script,
                    startedAtUtc,
                    completedAtUtc,
                    durationMs,
                    "FAILED",
                    errorNumber,
                    errorMessage,
                    cancellationToken);
            }
            else
            {
                await UpdateRunAsync(
                    connection,
                    transaction,
                    runId.Value,
                    completedAtUtc,
                    durationMs,
                    "FAILED",
                    errorNumber,
                    errorMessage,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task InsertCompletedRunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid databaseIdentity,
        MigrationScript script,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMs,
        string outcome,
        int? errorNumber,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO [operations].[migration_runs]
            (
                [migration_sequence],
                [migration_name],
                [script_checksum],
                [database_identity],
                [environment],
                [application_version],
                [started_at_utc],
                [completed_at_utc],
                [duration_ms],
                [outcome],
                [executed_by],
                [error_number],
                [error_message]
            )
            VALUES
            (
                @sequence,
                @name,
                @checksum,
                @database_identity,
                @environment,
                @application_version,
                @started_at_utc,
                @completed_at_utc,
                @duration_ms,
                @outcome,
                COALESCE(SUSER_SNAME(), N'UNKNOWN'),
                @error_number,
                @error_message
            );
            """;
        AddMigrationParameters(command, databaseIdentity, script, startedAtUtc);
        command.Parameters.Add("@completed_at_utc", SqlDbType.DateTime2).Value = completedAtUtc;
        command.Parameters.Add("@duration_ms", SqlDbType.BigInt).Value = durationMs;
        command.Parameters.Add("@outcome", SqlDbType.VarChar, 20).Value = outcome;
        command.Parameters.Add("@error_number", SqlDbType.Int).Value =
            errorNumber is null ? DBNull.Value : errorNumber.Value;
        command.Parameters.Add("@error_message", SqlDbType.NVarChar, 4000).Value =
            errorMessage is null ? DBNull.Value : errorMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long runId,
        DateTime completedAtUtc,
        long durationMs,
        string outcome,
        int? errorNumber,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [operations].[migration_runs]
            SET
                [completed_at_utc] = @completed_at_utc,
                [duration_ms] = @duration_ms,
                [outcome] = @outcome,
                [error_number] = @error_number,
                [error_message] = @error_message
            WHERE [migration_run_id] = @run_id
              AND [outcome] = 'STARTED';

            IF @@ROWCOUNT <> 1
                THROW 50001, 'Migration run state changed unexpectedly.', 1;
            """;
        command.Parameters.Add("@run_id", SqlDbType.BigInt).Value = runId;
        command.Parameters.Add("@completed_at_utc", SqlDbType.DateTime2).Value = completedAtUtc;
        command.Parameters.Add("@duration_ms", SqlDbType.BigInt).Value = durationMs;
        command.Parameters.Add("@outcome", SqlDbType.VarChar, 20).Value = outcome;
        command.Parameters.Add("@error_number", SqlDbType.Int).Value =
            errorNumber is null ? DBNull.Value : errorNumber.Value;
        command.Parameters.Add("@error_message", SqlDbType.NVarChar, 4000).Value =
            errorMessage is null ? DBNull.Value : errorMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddMigrationParameters(
        SqlCommand command,
        Guid databaseIdentity,
        MigrationScript script,
        DateTime startedAtUtc)
    {
        command.Parameters.Add("@sequence", SqlDbType.Int).Value = script.Sequence;
        command.Parameters.Add("@name", SqlDbType.VarChar, 260).Value = script.Name;
        command.Parameters.Add("@checksum", SqlDbType.Char, 64).Value = script.Checksum;
        command.Parameters.Add("@database_identity", SqlDbType.UniqueIdentifier).Value = databaseIdentity;
        command.Parameters.Add("@environment", SqlDbType.VarChar, 30).Value = _options.Environment;
        command.Parameters.Add("@application_version", SqlDbType.VarChar, 100).Value =
            _options.ApplicationVersion is null ? DBNull.Value : _options.ApplicationVersion;
        command.Parameters.Add("@started_at_utc", SqlDbType.DateTime2).Value = startedAtUtc;
    }

    private static async Task RollBackOpenTransactionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record LedgerState(bool IsInitialized, Guid? DatabaseIdentity);

    private sealed record AppliedMigration(int Sequence, string Name, string Checksum);
}

public sealed record MigrationSummary(
    int DiscoveredCount,
    int PreviouslyAppliedCount,
    int PendingCount,
    int AppliedCount,
    bool WasDryRun);

public sealed class DatabaseLockException : Exception
{
    public DatabaseLockException(string message)
        : base(message)
    {
    }
}

public sealed class MigrationExecutionException : Exception
{
    public MigrationExecutionException(
        MigrationScript script,
        int batchStartLine,
        int repeatIndex,
        Exception innerException)
        : base(
            $"Migration {script.Name} failed in the batch starting at line {batchStartLine}" +
            (repeatIndex > 1 ? $" (GO repetition {repeatIndex})" : string.Empty) +
            $": {innerException.Message}",
            innerException)
    {
        Script = script;
        BatchStartLine = batchStartLine;
        RepeatIndex = repeatIndex;
    }

    public MigrationScript Script { get; }

    public int BatchStartLine { get; }

    public int RepeatIndex { get; }
}
