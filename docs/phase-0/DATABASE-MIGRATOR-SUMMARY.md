# Database Migrator Summary

## Purpose

`ThesisPulse.DatabaseMigrator` is the only approved executable for applying shared ThesisPulse AI SQL Server schema migrations.

It implements ADR-0009 without allowing EF Core migrations, Alembic or application startup to create a competing operational-schema history.

## Project layout

```text
ThesisPulseAI.sln
src/
  ThesisPulse.DatabaseMigrator/
    ThesisPulse.DatabaseMigrator.csproj
    Program.cs
    MigratorApplication.cs
    MigratorOptions.cs
    MigrationScript.cs
    SqlBatchSplitter.cs
    DatabaseMigrator.cs
    README.md

tests/
  database/
    dotnet/
      ThesisPulse.DatabaseMigrator.Tests.csproj
      Program.cs
```

## Migration discovery

The runner accepts only top-level migration files matching:

```text
V<zero-padded-sequence>__<lower_snake_case_description>.sql
```

It rejects:

- invalid filenames;
- duplicate sequences;
- duplicate names;
- sequence gaps;
- empty scripts;
- missing local scripts for applied database rows;
- name differences for an applied sequence;
- checksum differences for an applied migration.

Sequences must be contiguous from V0001.

## Stable checksums

Checksums use SHA-256 over canonical UTF-8 content.

Before hashing, the runner:

- removes an optional UTF-8 BOM;
- converts CRLF and CR line endings to LF.

This makes the checksum stable across Windows and Linux checkouts while still detecting meaningful script changes.

## SQL batches

The parser supports:

- standalone `GO` separators;
- `GO n` repeat counts;
- trailing comments on a `GO` line.

A line containing `GO` is not treated as a separator while the parser is inside:

- a single-quoted string;
- a double-quoted identifier;
- a bracketed identifier;
- a block comment.

Each batch receives the configured SQL command timeout. The runner stops immediately after the first failed batch.

## Exclusive migration ownership

The runner opens the explicitly named target database and acquires a session-owned exclusive SQL Server application lock:

```text
ThesisPulseAI.DatabaseMigrator:<database-name>
```

Only one runner can apply migrations to that database at a time. Closing the SQL session releases the lock even after an abnormal process failure.

## Ledger behavior

### `operations.schema_migrations`

A successful migration records:

- sequence;
- immutable filename;
- SHA-256 checksum;
- database identity;
- environment;
- application or deployment version;
- UTC applied timestamp;
- duration;
- SQL execution identity.

Already-applied rows are validated and skipped. They are never executed again.

### `operations.migration_runs`

When the metadata foundation already exists, each attempted pending migration first creates a `STARTED` row. Success changes it to `SUCCEEDED`; failure changes it to `FAILED` with the SQL error number and a bounded error message.

For a completely empty database, V0001 creates the metadata foundation. The migrator then records V0001 as a completed successful run and authoritative applied migration.

## Existing manually migrated database

The project initially applied V0001–V0009 using `sqlcmd`. Those scripts created the metadata tables but did not register themselves in `operations.schema_migrations`.

The first migrator run therefore:

1. sees an initialized but empty ledger;
2. treats V0001–V0009 as pending;
3. executes the repeat-safe scripts;
4. registers all nine names and checksums.

The second run must report that the database is current and execute zero migrations.

## Configuration boundary

The connection string comes from either:

```text
THESISPULSE_DATABASE_CONNECTION
```

or the explicit `--connection` option. It is never printed.

The database must already be named in the connection string. The migrator does not create databases implicitly.

Allowed deployment environments match the V0001 ledger constraint:

```text
LOCAL
DEVELOPMENT
TEST
PAPER
SHADOW
RESTRICTED_LIVE
LIVE
```

## Dry run

`--dry-run` still:

- discovers and hashes scripts;
- opens the target database;
- acquires the application lock;
- inspects the migration ledger;
- validates applied names and checksums;
- reports pending scripts.

It does not execute SQL migration batches or write ledger rows.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Successful apply or dry run |
| 1 | Unexpected process failure |
| 2 | Configuration or argument failure |
| 3 | Migration file/history/checksum validation failure |
| 4 | SQL connection or migration-lock failure |
| 5 | Migration batch execution failure |
| 130 | Operator cancellation |

## Tests

The dependency-free console tests cover:

- checksum equality across LF and CRLF;
- normal `GO` splitting;
- `GO n` repetition;
- `GO` inside block comments;
- `GO` inside multiline strings;
- ordered discovery;
- sequence-gap rejection;
- invalid-filename rejection.

Database integration acceptance remains local because this repository currently has no SQL Server CI environment.

## Local acceptance gates

Before marking the migrator complete:

1. restore and build `ThesisPulseAI.sln` in Release;
2. run the contract fixtures;
3. run the migrator parser/discovery tests;
4. perform a dry run against LocalDB;
5. apply migrations once and confirm nine ledger rows;
6. run the migrator again and confirm zero scripts execute;
7. run V0009 structural verification;
8. test a copied script with a changed checksum against a disposable database and confirm exit code 3;
9. start two migrators against a disposable database and confirm only one obtains the lock;
10. retain the migration principal as the only identity with shared-schema DDL permission.

## Deferred work

Later batches will add:

- automated SQL Server integration tests;
- least-privilege migration-principal provisioning;
- deployment-pipeline invocation;
- schema-drift verification;
- abandoned-run reconciliation policy;
- structured telemetry and operational alerts for migration deployment;
- reviewed deterministic reference and policy seed runners.
