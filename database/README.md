# ThesisPulse AI Database

This directory is the single migration authority for the shared ThesisPulse AI SQL Server operational database.

## Rules

- Ordered SQL scripts are authoritative.
- `ThesisPulse.DatabaseMigrator` is the only approved runner for shared operational migrations.
- EF Core migrations and Alembic must not independently alter the shared operational schema.
- No application or worker may create or migrate the database during startup.
- Applied migrations are immutable and production migrations are forward-only by default.
- Runtime database principals do not receive DDL permissions.
- Prices, quantities, capital, risk, fees, P&L, scores, probabilities and confidence use fixed-precision decimals rather than `float`.
- Canonical timestamps use UTC `datetime2(7)` columns.
- Scripts that create filtered indexes declare the required SQL Server session options explicitly.
- Signals and theses never authorize execution; independent risk decisions and trade plans are mandatory.
- Approved quantity, risk, price tolerance and protective stops may only become stricter downstream.
- Execution intent is persisted before broker contact.
- Unknown post-submission outcomes require reconciliation; blind retries are prohibited.
- Operational history is append-only; current state is a projection.
- Durable messages use at-least-once delivery with inbox/outbox idempotency.
- The most restrictive active operational control wins, while safe risk-reducing exits remain available.
- Broker credentials and access tokens are never stored in migrations, operational contracts, alerts or audit payloads.

## Structure

```text
database/
  migrations/
  seeds/
    reference/
    development/
  verification/
  rollback/
  README.md

src/
  ThesisPulse.DatabaseMigrator/
```

## Migration naming

```text
V<zero-padded-sequence>__<lower_snake_case_description>.sql
```

Sequences are global and contiguous from V0001. Once applied, a script must not be edited. Corrections require a new migration.

## Implemented migrations

| Migration | Foundation | Verification |
|---|---|---|
| V0001 | Schemas and migration metadata | `database/verification/V0001__verify_schemas_and_migration_metadata.sql` |
| V0002 | Exchanges, instruments, calendars, universes and broker mappings | `database/verification/V0002__verify_reference_tables.sql` |
| V0003 | Market observations, candles, ingestion and quality | `database/verification/V0003__verify_market_data_tables.sql` |
| V0004 | Intelligence engines, outputs, signals and lineage | `database/verification/V0004__verify_intelligence_and_signal_tables.sql` |
| V0005 | Theses, evidence, scenarios, invalidation and failure fingerprints | `database/verification/V0005__verify_thesis_tables.sql` |
| V0006 | Risk policies, snapshots, decisions and trade plans | `database/verification/V0006__verify_risk_and_trade_plan_tables.sql` |
| V0007 | Execution commands, orders, fills and reconciliation | `database/verification/V0007__verify_execution_and_reconciliation_tables.sql` |
| V0008 | Portfolio positions, lots, cash/exposure ledgers, valuations and P&L | `database/verification/V0008__verify_portfolio_and_pnl_tables.sql` |
| V0009 | Inbox/outbox, jobs, controls, incidents, alerts and audit | `database/verification/V0009__verify_operational_foundation_tables.sql` |

V0001 through V0009 form the initial Phase 0 SQL Server storage baseline.

## Build and test the migrator

From the repository root:

```powershell
cd "D:\00 Projects\ThesisPulseAI"

dotnet restore ".\ThesisPulseAI.sln"
dotnet build ".\ThesisPulseAI.sln" --configuration Release --no-restore

dotnet run `
  --project ".\tests\database\dotnet\ThesisPulse.DatabaseMigrator.Tests.csproj" `
  --configuration Release
```

## Configure LocalDB

The target database must already exist. Keep the connection string out of source control and command history where possible.

```powershell
$env:THESISPULSE_DATABASE_CONNECTION = `
  "Server=(localdb)\MSSQLLocalDB;Database=ThesisPulseAI;Integrated Security=true;Encrypt=false"

$env:THESISPULSE_MIGRATION_ENVIRONMENT = "LOCAL"
$env:THESISPULSE_APPLICATION_VERSION = "local-dev"
```

`Encrypt=false` is permitted only for local LocalDB development. Shared environments follow the security and TLS policy.

## Dry run

```powershell
dotnet run `
  --project ".\src\ThesisPulse.DatabaseMigrator\ThesisPulse.DatabaseMigrator.csproj" `
  --configuration Release `
  --no-build `
  -- `
  --dry-run
```

The dry run opens the database, acquires the migration lock, validates filenames and sequence continuity, and compares repository checksums with the database ledger. It does not execute pending SQL.

## Apply migrations

```powershell
dotnet run `
  --project ".\src\ThesisPulse.DatabaseMigrator\ThesisPulse.DatabaseMigrator.csproj" `
  --configuration Release `
  --no-build
```

The migrator:

1. discovers and checksums ordered scripts;
2. opens the explicitly named database;
3. acquires a session-scoped exclusive `sp_getapplock`;
4. validates applied names and checksums;
5. executes pending SQL batches in order;
6. records attempts in `operations.migration_runs`;
7. records successful history in `operations.schema_migrations`;
8. stops on the first failure and returns a non-zero exit code.

### Existing LocalDB bootstrap

V0001–V0009 were initially executed manually with `sqlcmd`. Therefore the first migrator run may list all nine scripts as `PENDING` even though their tables already exist. This is expected when `operations.schema_migrations` is empty. The repeat-safe scripts run again and populate the authoritative ledger.

Run the migrator a second time. Expected result:

```text
Database is current. No migrations were executed.
```

## Inspect the authoritative ledger

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -Q "SELECT migration_sequence, migration_name, script_checksum, environment, applied_at_utc, duration_ms FROM operations.schema_migrations ORDER BY migration_sequence;"
```

Expected after bootstrap: nine rows with sequences 1 through 9.

## Verification scripts

Verification scripts remain explicit acceptance checks and are not silently executed by application startup. Run the latest structural verification after migration:

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0009__verify_operational_foundation_tables.sql"
```

Expected result:

```text
verification_status  migration_version  verified_table_count
PASS                 V0009              19
```

## Verification expectations

Every migration set must be tested against an empty database, the previous supported version, representative data and least-privilege runtime principals. Verification includes objects, trusted constraints, indexes, checksums, repeat execution and application mapping smoke tests.

## Related decisions

- `docs/adr/ADR-0001-system-architecture-and-technology-ownership.md`
- `docs/adr/ADR-0007-aspnet-core-python-integration-model.md`
- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0017-audit-traceability-and-decision-lineage.md`
- `docs/adr/ADR-0018-security-credentials-and-secret-management.md`
- `docs/adr/ADR-0019-failure-handling-and-kill-switch-policy.md`
- `docs/broker/UPSTOX-RECONCILIATION-POLICY.md`
