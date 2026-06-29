# ThesisPulse AI Database

This directory is the single migration authority for the shared ThesisPulse AI SQL Server operational database.

## Rules

- Ordered SQL scripts are authoritative.
- EF Core migrations and Alembic must not independently alter the shared operational schema.
- No application or worker may create or migrate the database during startup.
- Applied migrations are immutable and production migrations are forward-only by default.
- Runtime database principals do not receive DDL permissions.
- Prices, quantities, capital, risk, fees, P&L, scores, probabilities and confidence use fixed-precision decimals rather than `float`.
- Canonical timestamps use UTC `datetime2(7)` columns.
- Scripts that create filtered indexes declare the required SQL Server session options explicitly.
- Signals and theses never authorize execution; independent risk decisions and trade plans are mandatory.
- Approved quantity, risk and protective stops may only become stricter downstream.
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

Creates the business schemas plus migration metadata and run tracking.

Verification: `database/verification/V0001__verify_schemas_and_migration_metadata.sql`

### V0002 — reference tables

Creates versioned exchanges, calendars, sessions, instruments, universes, brokers and broker-instrument mappings.

Verification: `database/verification/V0002__verify_reference_tables.sql`

### V0003 — market data, candles and quality state

Creates market sources, ingestion batches, immutable source observations, normalized candle revisions, ingestion cursors and canonical quality assessments.

Verification: `database/verification/V0003__verify_market_data_tables.sql`

### V0004 — intelligence outputs and canonical signals

Creates engine registration and runs, immutable outputs, input/evidence lineage, canonical signals, fusion lineage and append-only signal status events.

Verification: `database/verification/V0004__verify_intelligence_and_signal_tables.sql`

### V0005 — theses and falsification lifecycle

Creates immutable theses, related-signal lineage, normalized evidence, assumptions, scenarios, invalidation definitions/events, status history and governed failure fingerprints.

Verification: `database/verification/V0005__verify_thesis_tables.sql`

### V0006 — risk decisions and trade plans

Creates:

- `risk.risk_policies`
- `risk.risk_policy_mandatory_rules`
- `risk.risk_policy_status_events`
- `risk.active_policy_assignments`
- `risk.capital_snapshots`
- `risk.portfolio_snapshots`
- `risk.portfolio_snapshot_positions`
- `risk.portfolio_snapshot_exposures`
- `risk.risk_decisions`
- `risk.risk_decision_reason_codes`
- `risk.risk_decision_targets`
- `risk.risk_decision_limit_checks`
- `risk.trade_plans`
- `risk.trade_plan_targets`
- `risk.trade_plan_status_events`

V0006 preserves immutable policy definitions, active-scope assignment, exact capital and portfolio evidence, requested-versus-approved risk and quantity, every limit check, mandatory stop protection and the maximum execution envelope. Hard-limit policy definitions must permit risk-reducing exits.

Verification: `database/verification/V0006__verify_risk_and_trade_plan_tables.sql`

## LocalDB execution

Run all commands from the repository root:

```powershell
cd "D:\00 Projects\ThesisPulseAI"
```

The database must already exist. `-b` returns a non-zero exit code for SQL errors. `-I` enables quoted identifiers for the `sqlcmd` session.

### V0001

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

### V0002

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

### V0003

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

### V0004

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

### V0005

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

### V0006

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0006__create_risk_and_trade_plan_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0006__verify_risk_and_trade_plan_tables.sql"
```

Expected V0006 result:

```text
verification_status  migration_version  verified_table_count
-------------------  -----------------  --------------------
PASS                 V0006              15
```

Run each new migration and its verification script a second time to confirm repeat execution succeeds without duplicate objects.

## Initial migration sequence

1. schemas and migration metadata;
2. reference instruments, calendars, universes and broker mappings;
3. market observations, candles, ingestion state and quality assessments;
4. intelligence engine outputs and signals;
5. theses, evidence, scenarios, invalidation and failure fingerprints;
6. risk policies, snapshots, decisions and trade plans;
7. execution commands, orders, events and fills;
8. portfolio positions, exposure and P&L ledgers;
9. inbox, outbox, jobs, audit events, incidents and kill switches.

## Required migration header

Each script documents purpose, dependencies, runtime impact, locking, compatibility, data movement, verification and recovery.

## Planned migrator

The planned `ThesisPulse.DatabaseMigrator` .NET console application will acquire a SQL Server application lock, validate checksums, execute pending scripts in order, record UTC metadata, stop on first failure and return a non-zero exit code on failure.

Until it is implemented, local scripts are executed explicitly with `sqlcmd` or SSMS. Connection strings and credentials must come from secret configuration and must not be committed.

## Verification expectations

Every migration set must be verified against an empty database, the previous supported version, representative data and least-privilege .NET and Python runtime principals. Verification includes objects, trusted constraints, indexes, checksums, model mapping, repeat execution and end-to-end lineage.

## Related decisions

- `docs/adr/ADR-0006-capital-and-risk-limits.md`
- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0010-timestamp-timezone-and-exchange-calendar.md`
- `docs/adr/ADR-0011-canonical-engine-output-and-signal-contracts.md`
- `docs/adr/ADR-0012-thesis-risk-decision-and-trade-plan-contracts.md`
- `docs/adr/ADR-0016-live-loss-learning-and-promotion-governance.md`
- `docs/adr/ADR-0019-failure-handling-and-kill-switch-policy.md`
- `docs/adr/ADR-0020-market-data-quality-freshness-and-stale-data-policy.md`
