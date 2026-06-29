using Microsoft.Data.SqlClient;

namespace ThesisPulse.DatabaseMigrator;

public static class MigratorApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        var parseResult = MigratorOptions.Parse(args);
        if (parseResult.ShowHelp)
        {
            WriteHelp(Console.Out);
            return 0;
        }

        if (parseResult.Error is not null || parseResult.Options is null)
        {
            Console.Error.WriteLine($"Configuration error: {parseResult.Error}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
            Console.Error.WriteLine("Cancellation requested. The active SQL command will be cancelled.");
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var migrator = new DatabaseMigrator(parseResult.Options);
            await migrator.RunAsync(cancellationSource.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Migration cancelled.");
            return 130;
        }
        catch (MigrationValidationException exception)
        {
            Console.Error.WriteLine($"Migration validation failed: {exception.Message}");
            return 3;
        }
        catch (DatabaseLockException exception)
        {
            Console.Error.WriteLine($"Migration lock failed: {exception.Message}");
            return 4;
        }
        catch (MigrationExecutionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 5;
        }
        catch (SqlException exception)
        {
            Console.Error.WriteLine($"SQL Server error {exception.Number}: {exception.Message}");
            return 4;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected migrator failure: {exception}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public static void WriteHelp(TextWriter output)
    {
        output.WriteLine("ThesisPulse AI SQL Server Database Migrator");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  dotnet run --project src/ThesisPulse.DatabaseMigrator -- [options]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --connection <value>              SQL Server connection string.");
        output.WriteLine("  --environment <value>             LOCAL, DEVELOPMENT, TEST, PAPER, SHADOW,");
        output.WriteLine("                                      RESTRICTED_LIVE or LIVE. Default: LOCAL.");
        output.WriteLine("  --migrations <path>               Migration directory. Default: database/migrations.");
        output.WriteLine("  --application-version <value>     Deployment or application version recorded in the ledger.");
        output.WriteLine("  --command-timeout-seconds <value> Per-batch SQL timeout. Default: 120.");
        output.WriteLine("  --lock-timeout-seconds <value>    Application-lock wait. Default: 60.");
        output.WriteLine("  --dry-run                          Validate files, lock and ledger without applying scripts.");
        output.WriteLine("  --help, -h                         Show this help.");
        output.WriteLine();
        output.WriteLine("Environment variables:");
        output.WriteLine("  THESISPULSE_DATABASE_CONNECTION");
        output.WriteLine("  THESISPULSE_MIGRATION_ENVIRONMENT");
        output.WriteLine("  THESISPULSE_MIGRATIONS_PATH");
        output.WriteLine("  THESISPULSE_APPLICATION_VERSION");
        output.WriteLine("  THESISPULSE_MIGRATION_COMMAND_TIMEOUT_SECONDS");
        output.WriteLine("  THESISPULSE_MIGRATION_LOCK_TIMEOUT_SECONDS");
        output.WriteLine();
        output.WriteLine("The connection string is never printed. Prefer the environment variable or an approved");
        output.WriteLine("deployment secret provider instead of placing credentials on the command line.");
    }
}
