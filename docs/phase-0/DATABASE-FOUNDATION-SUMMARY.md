# Phase 0 Database Foundation Summary

## Migration

`database/migrations/V0001__create_schemas_and_migration_metadata.sql`

V0001 establishes the SQL Server ownership boundaries before any business tables are added.

## Created schemas

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

## Created metadata tables

### `operations.database_metadata`

Stores one database identity record, the database name, baseline schema version, audit fields and a row-version token.

### `operations.schema_migrations`

Stores successfully applied migrations with sequence, immutable script checksum, environment, version, execution duration and operator identity.

### `operations.migration_runs`

Stores every migration attempt including started, succeeded, failed and skipped outcomes, timestamps and bounded error information.

## Safety properties

- additive migration;
- transaction with `XACT_ABORT`;
- UTC timestamps;
- fixed migration ordering;
- 64-character hexadecimal checksum constraints;
- allowed-environment constraints;
- singleton database metadata;
- unique applied migration sequence and name;
- migration attempts separated from the successful ledger;
- no application or worker startup migration behavior.

## Verification

`database/verification/V0001__verify_schemas_and_migration_metadata.sql`

The verification script checks:

- every required schema;
- all three metadata tables;
- the singleton metadata row;
- database identity and baseline version;
- metadata foreign keys;
- required unique and query indexes.

## Local acceptance procedure

1. Run V0001 against an empty local `ThesisPulseAI` database.
2. Run the verification script and confirm `PASS`.
3. Run V0001 a second time.
4. Run verification again.
5. Confirm only one `operations.database_metadata` row exists.

The Phase 0 clean-database exit gate remains open until these steps are completed locally.

## Next migration

V0002 will add versioned reference data structures for exchanges, instruments, exchange calendars, sessions, universes and broker instrument mappings.
