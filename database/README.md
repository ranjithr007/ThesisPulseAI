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

## Planned structure

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
V0001__create_business_schemas.sql
V0002__create_reference_tables.sql
V0003__create_market_tables.sql
```

## Initial migration sequence

The first migration set should establish:

1. database schemas;
2. migration and deployment metadata;
3. reference instruments, exchanges, calendars, sessions, and broker mappings;
4. market candles and data-quality state;
5. intelligence engine outputs and signals;
6. theses and decision lineage;
7. risk policies, snapshots, decisions, and trade plans;
8. execution commands, orders, events, and fills;
9. portfolio positions, exposure, and P&L ledgers;
10. inbox, outbox, jobs, audit events, incidents, and kill switches.

## Required migration header

Each script should document:

- purpose;
- dependencies;
- expected runtime impact;
- locking considerations;
- backward-compatibility window;
- data migration requirements;
- verification script;
- rollback or roll-forward recovery plan.

## Local execution

The planned `ThesisPulse.DatabaseMigrator` .NET console application will:

- connect using a dedicated migration credential;
- acquire a migration lock;
- validate applied-script checksums;
- execute pending scripts in sequence;
- record UTC execution metadata;
- stop on first failure;
- return a non-zero process exit code on failure.

Connection strings and credentials must come from environment-specific secret configuration and must not be committed.

## Verification

Every migration set must be verified against:

- an empty SQL Server database;
- the previous supported schema version;
- representative existing data;
- the least-privilege .NET runtime principal;
- the least-privilege Python runtime principal.

Verification must include schema objects, constraints, indexes, checksums, model mapping smoke tests, and repeat execution.

## Related decisions

- `docs/adr/ADR-0008-sql-server-schema-and-naming-conventions.md`
- `docs/adr/ADR-0009-database-migration-ownership.md`
- `docs/adr/ADR-0010-timestamp-timezone-and-exchange-calendar.md`
