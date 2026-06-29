# ADR-0009: Database Migration Ownership

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture and data
- **Supersedes:** None

## Context

ThesisPulse AI uses ASP.NET Core with EF Core and Python with SQLAlchemy. Allowing both EF Core migrations and Alembic to independently manage the same SQL Server operational schema would create competing histories, branching heads, deployment-order ambiguity, and destructive drift.

## Decision

ThesisPulse AI will use one centralized, repository-controlled SQL Server migration authority for the shared operational database.

A dedicated .NET console project named `ThesisPulse.DatabaseMigrator` will apply ordered, reviewed SQL scripts. The initial implementation will use DbUp or an equivalent script-based migration runner approved by the architecture team.

EF Core migrations and Alembic are not authoritative for the shared operational schema and must not run automatically in any environment.

## Repository structure

```text
database/
  migrations/
    V0001__create_schemas.sql
    V0002__create_reference_tables.sql
    V0003__create_market_tables.sql
    V0004__create_intelligence_tables.sql
    V0005__create_thesis_tables.sql
    V0006__create_risk_tables.sql
    V0007__create_execution_tables.sql
    V0008__create_portfolio_tables.sql
    V0009__create_operations_and_audit_tables.sql
  seeds/
    reference/
    development/
  verification/
  rollback/
  README.md

src/
  ThesisPulse.DatabaseMigrator/
```

The exact sequence evolves as the design matures, but there is always one global ordered history for the operational database.

## Migration naming

Migration files follow:

```text
V<zero-padded-sequence>__<lower_snake_case_description>.sql
```

Examples:

- `V0010__add_signal_expiry_index.sql`;
- `V0011__create_broker_reconciliation_runs.sql`;
- `V0012__add_trade_plan_contract_version.sql`.

Once merged to `main` or applied to a shared environment, an existing migration file is immutable. Corrections require a new migration.

## Migration ledger

The migrator maintains an authoritative ledger containing:

- migration sequence and name;
- script checksum;
- applied timestamp in UTC;
- environment and database identity;
- application or pipeline version;
- execution duration;
- outcome;
- operator or deployment identity.

A checksum mismatch for an already-applied migration is a deployment failure.

## Deployment policy

- Migrations run through CI/CD or an explicitly invoked deployment step.
- Application and worker processes do not mutate schema during startup.
- Only the migration principal has DDL permissions.
- Runtime principals use least-privilege DML permissions.
- Production deployment acquires a migration lock so only one migrator instance can run.
- The migrator stops on the first failed migration.
- A failed production migration blocks application promotion until resolved.

## Forward-only production strategy

Production migrations are forward-only by default.

Every material schema change must include:

- deployment-order requirements;
- backward-compatibility analysis;
- data-migration approach;
- verification queries;
- expected locking and runtime impact;
- rollback or roll-forward recovery plan;
- application compatibility window.

Rollback scripts may be provided for emergency use but are not executed automatically. For changes that lose data, the preferred recovery is restore or roll-forward from a tested backup rather than an unreviewed down migration.

## Expand-and-contract changes

Breaking changes use an expand-and-contract sequence:

1. add new nullable columns, tables, or compatible structures;
2. deploy application code that can read old and new forms;
3. backfill data in controlled batches;
4. switch writes to the new form;
5. verify correctness and reconciliation;
6. remove old structures in a later release.

Renames are implemented as expand, copy/backfill, switch, and later removal rather than immediate destructive rename when zero-downtime compatibility is required.

## Data migrations

Large backfills are separated from short DDL migrations when necessary.

A data migration must define:

- batch size;
- restartability;
- idempotency;
- progress tracking;
- throttling;
- lock and log-growth impact;
- validation and reconciliation queries;
- failure recovery.

Long-running backfills must not hold a single transaction for the entire dataset.

## ORM responsibilities

EF Core and SQLAlchemy may:

- define explicit mappings to the approved schema;
- validate model compatibility in tests;
- generate local draft SQL for developer review;
- help compare expected and actual schemas.

They may not:

- apply migrations to shared environments;
- create or drop the operational database at startup;
- independently maintain migration histories for shared tables;
- silently alter precision, nullability, constraints, indexes, or naming.

For isolated research databases that are not part of the operational source of truth, Alembic may be used only when the database and ownership boundary are explicitly documented.

## Seed data

Seed categories are separated:

- reference seeds: versioned, deterministic, and safe for controlled environments;
- development seeds: local or test-only;
- production operational data: never inserted through development seed scripts.

Secrets, live account identifiers, access tokens, and personal data are prohibited in repository seed files.

## Verification

Every migration set must be tested against:

- a clean SQL Server database;
- the previous supported schema version;
- representative existing data;
- expected runtime principals and permissions.

Verification includes:

- schema and object existence;
- constraint and index checks;
- data-count and reconciliation checks;
- migration-ledger checksums;
- application mapping smoke tests from .NET and Python;
- repeat execution proving already-applied scripts are not re-run.

## Drift detection

CI/CD or an operational verification job must detect unapproved schema drift by comparing the deployed schema with the migration-defined expected state.

Manual production DDL is prohibited except under an incident procedure. Emergency changes must be captured immediately as a versioned migration and reviewed after the incident.

## Alternatives considered

### EF Core migrations for .NET tables and Alembic for Python tables in the same database

Rejected because schema dependencies cross runtime boundaries and two histories can conflict.

### Automatic ORM migration at service startup

Rejected because concurrent startup, partial failure, and excessive runtime permissions create unacceptable operational risk.

### Manual SQL changes without a migration runner

Rejected because applied state, ordering, checksum validation, and repeatability would be unreliable.

## Consequences

- Developers must write or review explicit SQL migrations.
- The database has one unambiguous history.
- EF Core and SQLAlchemy remain mapping tools rather than competing schema authorities.
- Production deployments gain deterministic verification and drift control.
- Schema changes require stronger review but avoid recurring Alembic-head and cross-framework migration conflicts.
