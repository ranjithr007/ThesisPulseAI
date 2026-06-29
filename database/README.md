# ThesisPulse AI Database

This directory is the single migration authority for the shared ThesisPulse AI SQL Server operational database.

## Rules

- Ordered SQL scripts are authoritative.
- EF Core migrations and Alembic must not independently alter the shared operational schema.
- No application or worker may create or migrate the database during startup.
- Applied migrations are immutable.
- Production migrations are forward-only by default.
- Runtime database principals do not receive DDL permissions.
- Prices, quantities, capital, risk, fees, and P&L use fixed-precision decimals rather than `float`.
- Canonical timestamps use UTC `datetime2(7)` columns.

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
```

## Migration naming

```text
V<zero-padded-sequence>__<lower_snake_case_description>.sql
```

Example:

```text
V0001__create_schemas_and_migration_metadata.sql
V0002__create_reference_tables.sql
V0003__create_market_tables.sql
```

## Implemented migrations

### V0001 — schemas and migration metadata

Creates these business schemas:

- `reference`
- `market`
- `intelligence`
- `thesis`
- `risk`
- `execution`
- `portfolio`
- `broker`
- `ml`
- `backtest`
- `operations`
- `audit`

Creates:

- `operations.database_metadata` — singleton database identity and baseline version;
- `operations.schema_migrations` — authoritative successfully applied migration ledger;
- `operations.migration_runs` — migration attempts, outcomes, duration and error context.

Verification:

```text
database/verification/V0001__verify_schemas_and_migration_metadata.sql
```

## Run V0001 locally

Run from the repository root. Replace the server and database values with your local SQL Server configuration.

### SQL Server Express with Windows authentication

```powershell
sqlcmd `
  -S ".\SQLEXPRESS" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -i ".\database\migrations\V0001__create_schemas_and_migration_metadata.sql"
```

### SQL Server LocalDB

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -i ".\database\migrations\V0001__create_schemas_and_migration_metadata.sql"
```

The database must already exist. The command returns a non-zero exit code when SQL Server reports an error because `-b` is enabled.

## Verify V0001

```powershell
sqlcmd `
  -S ".\SQLEXPRESS" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -i ".\database\verification\V0001__verify_schemas_and_migration_metadata.sql"
```

Expected result:

```text
verification_status  migration_version
-------------------  -----------------
PASS                 V0001
```

Run the migration a second time, followed by verification, to confirm that local repeat execution does not create duplicate objects or metadata rows.

## Initial migration sequence

The migration history will establish:

1. database schemas and migration metadata;
2. reference instruments, exchanges, calendars, sessions, universes and broker mappings;
3. market candles and data-quality state;
4. intelligence engine outputs and signals;
5. theses and decision lineage;
6. risk policies, snapshots, decisions and trade plans;
7. execution commands, orders, events and fills;
8. portfolio positions, exposure and P&L ledgers;
9. inbox, outbox, jobs, audit events, incidents and kill switches.

## Required migration header

Each script documents:

- purpose;
- dependencies;
- expected runtime impact;
- locking considerations;
- backward-compatibility window;
- data migration requirements;
- verification script;
- rollback or roll-forward recovery plan.

## Planned migrator

The planned `ThesisPulse.DatabaseMigrator` .NET console application will:

- connect using a dedicated migration credential;
- acquire a SQL Server application lock;
- validate applied-script checksums;
- execute pending scripts in sequence;
- record UTC execution metadata;
- stop on first failure;
- return a non-zero process exit code on failure.

Until that migrator is implemented, local scripts are executed explicitly with `sqlcmd` or SSMS. Manual local execution does not replace the migration ledger behavior required for shared environments.

Connection strings and credentials must come from environment-specific secret configuration and must not be committed.

## Verification expectations

Every migration set must be verified against:

- an empty SQL Server database;
- the previous supported schema version;
- representative existing data;
- the least-privilege .NET runtime principal;
- the least-privilege Python runtime principal.

Verification includes schema objects, constraints, indexes, checksums, model mapping smoke tests, and repeat execution.

## Related decisions

- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0010-timestamp-timezone-and-exchange-calendar.md`
