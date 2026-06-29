# ThesisPulse AI Database

This directory is the single migration authority for the shared ThesisPulse AI SQL Server operational database.

## Rules

- Ordered SQL scripts are authoritative.
- EF Core migrations and Alembic must not independently alter the shared operational schema.
- No application or worker may create or migrate the database during startup.
- Applied migrations are immutable.
- Production migrations are forward-only by default.
- Runtime database principals do not receive DDL permissions.
- Prices, quantities, capital, risk, fees, P&L, scores, probabilities and confidence use fixed-precision decimals rather than `float`.
- Canonical timestamps use UTC `datetime2(7)` columns.
- Scripts that create filtered indexes declare the required SQL Server session options explicitly.
- Signals and theses never authorize execution; risk and trade-plan stages remain separate.
- Losses and failed theses create governed evidence only; they cannot directly mutate live production settings.

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

## Implemented migrations

### V0001 — schemas and migration metadata

Creates the business schemas plus:

- `operations.database_metadata`
- `operations.schema_migrations`
- `operations.migration_runs`

Verification:

```text
database/verification/V0001__verify_schemas_and_migration_metadata.sql
```

### V0002 — reference tables

Creates versioned exchanges, calendars, sessions, instruments, universes, brokers and broker-instrument mappings.

Verification:

```text
database/verification/V0002__verify_reference_tables.sql
```

### V0003 — market data, candles and quality state

Creates market sources, ingestion batches, immutable source observations, normalized candle revisions, ingestion cursors and canonical quality assessments.

Verification:

```text
database/verification/V0003__verify_market_data_tables.sql
```

### V0004 — intelligence outputs and canonical signals

Creates engine registration and runs, immutable engine outputs, market/feature/evidence lineage, canonical signals, fusion lineage, confirmation timeframes and append-only signal status events.

Verification:

```text
database/verification/V0004__verify_intelligence_and_signal_tables.sql
```

### V0005 — theses and falsification lifecycle

Creates:

- `thesis.theses`
- `thesis.thesis_signal_relationships`
- `thesis.thesis_evidence`
- `thesis.thesis_assumptions`
- `thesis.thesis_invalidation_conditions`
- `thesis.thesis_invalidation_events`
- `thesis.thesis_scenarios`
- `thesis.thesis_status_events`
- `thesis.thesis_failure_fingerprints`

V0005 preserves the exact primary signal, related supporting/contradicting signals, regime engine output, hypothesis, assumptions, scenario probabilities, expected path, expiry, invalidation state, status history and failure fingerprints. A failure fingerprint is constrained from authorizing a production change.

Verification:

```text
database/verification/V0005__verify_thesis_tables.sql
```

## LocalDB execution

Run commands from the repository root:

```powershell
cd "D:\00 Projects\ThesisPulseAI"
```

The database must already exist. `-b` returns a non-zero exit code on SQL errors. `-I` enables quoted identifiers for the sqlcmd session.

### Run and verify V0001

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0001__create_schemas_and_migration_metadata.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0001__verify_schemas_and_migration_metadata.sql"
```

### Run and verify V0002

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0002__create_reference_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0002__verify_reference_tables.sql"
```

### Run and verify V0003

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0003__create_market_data_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0003__verify_market_data_tables.sql"
```

### Run and verify V0004

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0004__create_intelligence_and_signal_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0004__verify_intelligence_and_signal_tables.sql"
```

### Run and verify V0005

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0005__create_thesis_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0005__verify_thesis_tables.sql"
```

Expected V0005 result:

```text
verification_status  migration_version  verified_table_count
-------------------  -----------------  --------------------
PASS                 V0005              9
```

Run each new migration and its verification script a second time to confirm repeat execution succeeds without duplicate objects.

## Initial migration sequence

1. database schemas and migration metadata;
2. reference instruments, exchanges, calendars, sessions, universes and broker mappings;
3. market observations, candles, ingestion state and data-quality assessments;
4. intelligence engine outputs and signals;
5. theses, evidence, scenarios, invalidation and failure fingerprints;
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

Until that migrator is implemented, local scripts are executed explicitly with `sqlcmd` or SSMS. Manual local execution does not replace the migration-ledger behavior required for shared environments.

Connection strings and credentials must come from environment-specific secret configuration and must not be committed.

## Verification expectations

Every migration set must be verified against:

- an empty SQL Server database;
- the previous supported schema version;
- representative existing data;
- the least-privilege .NET runtime principal;
- the least-privilege Python runtime principal.

Verification includes schema objects, constraints, indexes, checksums, model-mapping smoke tests, repeat execution and end-to-end lineage checks.

## Related decisions

- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0010-timestamp-timezone-and-exchange-calendar.md`
- `docs/adr/ADR-0011-canonical-engine-output-and-signal-contracts.md`
- `docs/adr/ADR-0012-thesis-risk-decision-and-trade-plan-contracts.md`
- `docs/adr/ADR-0016-live-loss-learning-and-promotion-governance.md`
- `docs/adr/ADR-0020-market-data-quality-freshness-and-stale-data-policy.md`
