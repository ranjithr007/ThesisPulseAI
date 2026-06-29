using Microsoft.Data.SqlClient;

namespace ThesisPulse.DatabaseMigrator;

public sealed record MigratorOptions(
    string ConnectionString,
    string Environment,
    string MigrationsPath,
    string? ApplicationVersion,
    int CommandTimeoutSeconds,
    int LockTimeoutSeconds,
    bool DryRun)
{
    private static readonly HashSet<string> AllowedEnvironments = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOCAL",
        "DEVELOPMENT",
        "TEST",
        "PAPER",
        "SHADOW",
        "RESTRICTED_LIVE",
        "LIVE"
    };

    public static OptionsParseResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dryRun = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (argument.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return OptionsParseResult.Failure($"Unexpected argument '{argument}'.");
            }

            var separatorIndex = argument.IndexOf('=');
            string key;
            string value;

            if (separatorIndex > 2)
            {
                key = argument[2..separatorIndex];
                value = argument[(separatorIndex + 1)..];
            }
            else
            {
                key = argument[2..];
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return OptionsParseResult.Failure($"Argument '--{key}' requires a value.");
                }

                value = args[++index];
            }

            if (!IsKnownOption(key))
            {
                return OptionsParseResult.Failure($"Unknown option '--{key}'.");
            }

            values[key] = value;
        }

        if (showHelp)
        {
            return OptionsParseResult.Help();
        }

        var connectionString = ReadValue(
            values,
            "connection",
            "THESISPULSE_DATABASE_CONNECTION");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return OptionsParseResult.Failure(
                "A SQL Server connection string is required through --connection or THESISPULSE_DATABASE_CONNECTION.");
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                return OptionsParseResult.Failure(
                    "The connection string must include Database or Initial Catalog. The migrator never creates a database implicitly.");
            }
        }
        catch (ArgumentException exception)
        {
            return OptionsParseResult.Failure($"The SQL Server connection string is invalid: {exception.Message}");
        }

        var environment = ReadValue(
                values,
                "environment",
                "THESISPULSE_MIGRATION_ENVIRONMENT")
            ?? "LOCAL";
        environment = environment.Trim().ToUpperInvariant();

        if (!AllowedEnvironments.Contains(environment))
        {
            return OptionsParseResult.Failure(
                $"Unsupported environment '{environment}'. Allowed values: {string.Join(", ", AllowedEnvironments.Order())}.");
        }

        var migrationsPath = ReadValue(
            values,
            "migrations",
            "THESISPULSE_MIGRATIONS_PATH");

        if (string.IsNullOrWhiteSpace(migrationsPath))
        {
            migrationsPath = RepositoryLocator.FindMigrationsDirectory();
        }

        migrationsPath = Path.GetFullPath(migrationsPath);
        if (!Directory.Exists(migrationsPath))
        {
            return OptionsParseResult.Failure($"Migration directory does not exist: {migrationsPath}");
        }

        var applicationVersion = ReadValue(
            values,
            "application-version",
            "THESISPULSE_APPLICATION_VERSION");

        var commandTimeoutResult = ParsePositiveInt(
            ReadValue(values, "command-timeout-seconds", "THESISPULSE_MIGRATION_COMMAND_TIMEOUT_SECONDS"),
            defaultValue: 120,
            optionName: "command-timeout-seconds");
        if (commandTimeoutResult.Error is not null)
        {
            return OptionsParseResult.Failure(commandTimeoutResult.Error);
        }

        var lockTimeoutResult = ParsePositiveInt(
            ReadValue(values, "lock-timeout-seconds", "THESISPULSE_MIGRATION_LOCK_TIMEOUT_SECONDS"),
            defaultValue: 60,
            optionName: "lock-timeout-seconds");
        if (lockTimeoutResult.Error is not null)
        {
            return OptionsParseResult.Failure(lockTimeoutResult.Error);
        }

        return OptionsParseResult.Success(new MigratorOptions(
            connectionString,
            environment,
            migrationsPath,
            string.IsNullOrWhiteSpace(applicationVersion) ? null : applicationVersion.Trim(),
            commandTimeoutResult.Value,
            lockTimeoutResult.Value,
            dryRun));
    }

    private static bool IsKnownOption(string key) => key.ToLowerInvariant() switch
    {
        "connection" => true,
        "environment" => true,
        "migrations" => true,
        "application-version" => true,
        "command-timeout-seconds" => true,
        "lock-timeout-seconds" => true,
        _ => false
    };

    private static string? ReadValue(
        IReadOnlyDictionary<string, string> values,
        string optionName,
        string environmentVariable)
    {
        if (values.TryGetValue(optionName, out var value))
        {
            return value;
        }

        return System.Environment.GetEnvironmentVariable(environmentVariable);
    }

    private static IntegerParseResult ParsePositiveInt(
        string? rawValue,
        int defaultValue,
        string optionName)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new IntegerParseResult(defaultValue, null);
        }

        if (!int.TryParse(rawValue, out var parsed) || parsed <= 0)
        {
            return new IntegerParseResult(
                0,
                $"--{optionName} must be a positive integer.");
        }

        return new IntegerParseResult(parsed, null);
    }

    private sealed record IntegerParseResult(int Value, string? Error);
}

public sealed record OptionsParseResult(
    MigratorOptions? Options,
    string? Error,
    bool ShowHelp)
{
    public static OptionsParseResult Success(MigratorOptions options) => new(options, null, false);

    public static OptionsParseResult Failure(string error) => new(null, error, false);

    public static OptionsParseResult Help() => new(null, null, true);
}

public static class RepositoryLocator
{
    public static string FindMigrationsDirectory()
    {
        foreach (var startDirectory in CandidateStartDirectories())
        {
            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "database", "migrations");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate database/migrations. Supply --migrations or THESISPULSE_MIGRATIONS_PATH.");
    }

    private static IEnumerable<string> CandidateStartDirectories()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }
}
