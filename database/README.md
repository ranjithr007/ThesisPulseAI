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
- Order, fill, position, cash, exposure and P&L events are append-only; current state is a projection.
- Broker positions are reconciliation evidence and cannot destructively overwrite fill-derived portfolio history.
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

### V0008 — portfolio and P&L

Creates:

- `portfolio.portfolios`
- `portfolio.positions`
- `portfolio.position_events`
- `portfolio.position_lots`
- `portfolio.position_lot_closures`
- `portfolio.realized_pnl_entries`
- `portfolio.cash_balances`
- `portfolio.cash_ledger_entries`
- `portfolio.exposure_states`
- `portfolio.exposure_ledger_entries`
- `portfolio.valuation_marks`
- `portfolio.position_valuations`
- `portfolio.pnl_snapshots`
- `portfolio.pnl_snapshot_positions`
- `portfolio.broker_position_observations`
- `portfolio.position_reconciliation_states`
- `portfolio.position_reconciliation_events`

V0008 enforces fill idempotency at the position boundary, lot and closure identities, cash-balance arithmetic, exposure ledger ordering, valuation freshness eligibility, current projection concurrency, and exit-safe broker-position reconciliation.

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
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\migrations\V0007__create_execution_and_reconciliation_tables.sql"
sqlcmd -S "(localdb)\MSSQLLocalDB" -d "ThesisPulseAI" -E -b -I -i ".\database\verification\V0007__verify_execution_and_reconciliation_tables.sql"
```

### V0008

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0008__create_portfolio_and_pnl_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0008__verify_portfolio_and_pnl_tables.sql"
```

Expected V0008 result:

```text
verification_status  migration_version  verified_table_count
-------------------  -----------------  --------------------
PASS                 V0008              17
```

Run every new migration and its verification script a second time to confirm repeat execution succeeds without duplicate objects.

## Initial migration sequence

1. schemas and migration metadata;
2. reference instruments, calendars, universes and broker mappings;
3. market observations, candles, ingestion state and quality assessments;
4. intelligence engine outputs and signals;
5. theses, evidence, scenarios, invalidation and failure fingerprints;
6. risk policies, snapshots, decisions and trade plans;
7. execution commands, orders, events, fills and reconciliation;
8. portfolio positions, lots, cash/exposure ledgers, valuations and P&L;
9. inbox, outbox, jobs, audit events, incidents, alerts and kill switches.

## Planned migrator

The planned `ThesisPulse.DatabaseMigrator` .NET console application will acquire a SQL Server application lock, validate checksums, execute pending scripts in order, record UTC metadata, stop on first failure and return a non-zero exit code on failure.

Until it is implemented, local scripts are executed explicitly with `sqlcmd` or SSMS. Connection strings and credentials must come from secret configuration and must not be committed.

## Verification expectations

Every migration set must be verified against an empty database, the previous supported version, representative data and least-privilege .NET and Python runtime principals. Verification includes objects, trusted constraints, indexes, checksums, repeat execution and end-to-end lineage.

## Related decisions

- `docs/adr/ADR-0001-system-architecture-and-technology-ownership.md`
- `docs/adr/ADR-0006-capital-and-risk-limits.md`
- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0013-upstox-broker-adapter-boundary.md`
- `docs/adr/ADR-0014-order-idempotency-and-execution-lifecycle.md`
- `docs/adr/ADR-0017-audit-traceability-and-decision-lineage.md`
- `docs/adr/ADR-0019-failure-handling-and-kill-switch-policy.md`
- `docs/broker/UPSTOX-RECONCILIATION-POLICY.md`
