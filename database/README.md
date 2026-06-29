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
- Approved quantity, risk, price tolerance and protective stops may only become stricter downstream.
- Execution intent is persisted before broker contact.
- Unknown post-submission outcomes require reconciliation; blind retries are prohibited.
- Order and fill events are append-only; current order state is a projection.
- Broker credentials and access tokens are never stored in operational contracts or payload archives.

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

Creates market sources, immutable observations, normalized candle revisions, ingestion cursors and quality assessments.

Verification: `database/verification/V0003__verify_market_data_tables.sql`

### V0004 — intelligence outputs and canonical signals

Creates engine registration and runs, immutable outputs, evidence lineage, canonical signals and signal status history.

Verification: `database/verification/V0004__verify_intelligence_and_signal_tables.sql`

### V0005 — theses and falsification lifecycle

Creates immutable theses, related-signal lineage, evidence, assumptions, scenarios, invalidation events and failure fingerprints.

Verification: `database/verification/V0005__verify_thesis_tables.sql`

### V0006 — risk decisions and trade plans

Creates immutable risk policies, active assignments, capital and portfolio snapshots, deterministic decisions, limit evidence and trade plans.

Verification: `database/verification/V0006__verify_risk_and_trade_plan_tables.sql`

### V0007 — execution, orders, fills and reconciliation

Creates:

- `broker.broker_accounts`
- `execution.order_transition_policies`
- `execution.order_transition_rules`
- `execution.execution_commands`
- `execution.execution_command_events`
- `execution.execution_command_states`
- `execution.orders`
- `execution.order_events`
- `execution.order_event_quarantines`
- `execution.fills`
- `broker.broker_requests`
- `execution.reconciliation_runs`
- `execution.reconciliation_observations`
- `execution.reconciliation_discrepancies`
- `execution.reconciliation_resolutions`

V0007 enforces environment/account-scoped command idempotency, unique client and broker identities, command-type field rules, optimistic order versions, append-only order events, broker-sequence evidence, idempotent fill identities, unknown-outcome reconciliation and exit-safe discrepancy handling.

Verification: `database/verification/V0007__verify_execution_and_reconciliation_tables.sql`

## LocalDB execution

Run all commands from the repository root:

```powershell
cd "D:\00 Projects\ThesisPulseAI"
```

The database must already exist. `-b` returns a non-zero exit code for SQL errors. `-I` enables quoted identifiers for the `sqlcmd` session.

### V0001

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0001__create_schemas_and_migration_metadata.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0001__verify_schemas_and_migration_metadata.sql"
```

### V0002

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0002__create_reference_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0002__verify_reference_tables.sql"
```

### V0003

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0003__create_market_data_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0003__verify_market_data_tables.sql"
```

### V0004

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0004__create_intelligence_and_signal_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0004__verify_intelligence_and_signal_tables.sql"
```

### V0005

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0005__create_thesis_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0005__verify_thesis_tables.sql"
```

### V0006

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0006__create_risk_and_trade_plan_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0006__verify_risk_and_trade_plan_tables.sql"
```

### V0007

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0007__create_execution_and_reconciliation_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0007__verify_execution_and_reconciliation_tables.sql"
```

Expected V0007 result:

```text
verification_status  migration_version  verified_table_count
-------------------  -----------------  --------------------
PASS                 V0007              15
```

Run each new migration and its verification script a second time to confirm repeat execution succeeds without duplicate objects.

## Initial migration sequence

1. schemas and migration metadata;
2. reference instruments, calendars, universes and broker mappings;
3. market observations, candles, ingestion state and quality assessments;
4. intelligence engine outputs and signals;
5. theses, evidence, scenarios, invalidation and failure fingerprints;
6. risk policies, snapshots, decisions and trade plans;
7. execution commands, orders, events, fills and reconciliation;
8. portfolio positions, exposure and P&L ledgers;
9. inbox, outbox, jobs, audit events, incidents and kill switches.

## Planned migrator

The planned `ThesisPulse.DatabaseMigrator` .NET console application will acquire a SQL Server application lock, validate checksums, execute pending scripts in order, record UTC metadata, stop on first failure and return a non-zero exit code on failure.

Until it is implemented, local scripts are executed explicitly with `sqlcmd` or SSMS. Connection strings and credentials must come from secret configuration and must not be committed.

## Verification expectations

Every migration set must be verified against an empty database, the previous supported version, representative data and least-privilege .NET and Python runtime principals. Verification includes objects, trusted constraints, indexes, checksums, repeat execution and end-to-end lineage.

## Related decisions

- `docs/adr/ADR-0006-capital-and-risk-limits.md`
- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0012-thesis-risk-decision-and-trade-plan-contracts.md`
- `docs/adr/ADR-0013-upstox-broker-adapter-boundary.md`
- `docs/adr/ADR-0014-order-idempotency-and-execution-lifecycle.md`
- `docs/adr/ADR-0017-audit-traceability-and-decision-lineage.md`
- `docs/adr/ADR-0019-failure-handling-and-kill-switch-policy.md`
