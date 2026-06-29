# ThesisPulse.DatabaseMigrator

The dedicated ThesisPulse AI SQL Server migration authority.

It discovers ordered scripts from `database/migrations`, validates immutable checksums, acquires an exclusive SQL Server application lock, executes pending scripts in order, records attempts in `operations.migration_runs`, records successful history in `operations.schema_migrations`, and stops on the first failure.

## Safety properties

- The target database must already exist.
- Application and worker startup never invokes this tool automatically.
- Only files matching `V<sequence>__<lower_snake_case_description>.sql` are accepted.
- Sequences must be contiguous from V0001.
- Checksums are SHA-256 over UTF-8 content with normalized LF line endings.
- Editing an applied migration causes a hard checksum failure.
- SQL `GO` and `GO n` batches are supported.
- A session-scoped `sp_getapplock` prevents concurrent migrators.
- Each batch has a bounded command timeout.
- The runner stops on the first failed migration.
- Connection strings are not printed.
- Existing databases that were migrated manually can bootstrap the ledger because V0001–V0009 are repeat-safe.

## Build

From the repository root:

```powershell
dotnet restore .\ThesisPulseAI.sln
dotnet build .\ThesisPulseAI.sln --configuration Release --no-restore
```

## Parser and discovery tests

```powershell
dotnet run `
  --project ".\tests\database\dotnet\ThesisPulse.DatabaseMigrator.Tests.csproj" `
  --configuration Release
```

## LocalDB connection

For local development, place the connection string in an environment variable rather than the command line:

```powershell
$env:THESISPULSE_DATABASE_CONNECTION = `
  "Server=(localdb)\MSSQLLocalDB;Database=ThesisPulseAI;Integrated Security=true;Encrypt=false"

$env:THESISPULSE_MIGRATION_ENVIRONMENT = "LOCAL"
$env:THESISPULSE_APPLICATION_VERSION = "local-dev"
```

`Encrypt=false` is for LocalDB development only. Shared and live SQL Server connections must follow the environment security policy.

## Validate without applying

```powershell
dotnet run `
  --project ".\src\ThesisPulse.DatabaseMigrator\ThesisPulse.DatabaseMigrator.csproj" `
  --configuration Release `
  --no-build `
  -- `
  --dry-run
```

The dry run still opens SQL Server, acquires the migration lock, validates repository ordering, and compares the database ledger and checksums. It does not execute pending scripts.

## Apply migrations

```powershell
dotnet run `
  --project ".\src\ThesisPulse.DatabaseMigrator\ThesisPulse.DatabaseMigrator.csproj" `
  --configuration Release `
  --no-build
```

On the first run against the current LocalDB, the migrator may show V0001–V0009 as `PENDING` even though their objects already exist. This is expected when those scripts were previously run with `sqlcmd` and the ledger was not populated. The scripts execute repeat-safely and are then registered.

Run the same command a second time. Expected final message:

```text
Database is current. No migrations were executed.
```

## Inspect the ledger

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -Q "SELECT migration_sequence, migration_name, script_checksum, environment, applied_at_utc, duration_ms FROM operations.schema_migrations ORDER BY migration_sequence;"
```

Expected after the initial bootstrap:

- nine rows;
- sequences 1 through 9;
- one immutable name and checksum per migration;
- environment `LOCAL` for the local run.

Inspect attempts:

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -Q "SELECT migration_sequence, migration_name, outcome, started_at_utc, completed_at_utc, duration_ms, error_number, error_message FROM operations.migration_runs ORDER BY migration_run_id;"
```

## Options

```text
--connection <value>
--environment <value>
--migrations <path>
--application-version <value>
--command-timeout-seconds <value>
--lock-timeout-seconds <value>
--dry-run
--help
```

Allowed environments:

```text
LOCAL
DEVELOPMENT
TEST
PAPER
SHADOW
RESTRICTED_LIVE
LIVE
```

## Environment variables

```text
THESISPULSE_DATABASE_CONNECTION
THESISPULSE_MIGRATION_ENVIRONMENT
THESISPULSE_MIGRATIONS_PATH
THESISPULSE_APPLICATION_VERSION
THESISPULSE_MIGRATION_COMMAND_TIMEOUT_SECONDS
THESISPULSE_MIGRATION_LOCK_TIMEOUT_SECONDS
```

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Successful run or successful dry run |
| 1 | Unexpected failure |
| 2 | Invalid configuration or arguments |
| 3 | Migration ordering, naming, history, or checksum validation failure |
| 4 | SQL connection or migration-lock failure |
| 5 | Migration script execution failure |
| 130 | Cancelled by the operator |

## Production boundary

The migrator must be invoked as an explicit deployment step using a dedicated migration principal. Runtime services must not receive DDL permissions and must not invoke the migrator during startup.
