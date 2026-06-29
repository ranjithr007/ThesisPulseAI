/*
Migration: V0001__create_schemas_and_migration_metadata.sql
Purpose:
  Create the ThesisPulse AI business schemas and migration metadata tables.
Dependencies:
  Empty or existing SQL Server database with dbo ownership available to the migration principal.
Expected runtime impact:
  Metadata-only DDL. No business-data scan or backfill.
Locking considerations:
  Schema modification locks are acquired while schemas, tables, constraints, and indexes are created.
Backward-compatibility window:
  Fully additive. Existing application tables are not modified.
Data migration requirements:
  None.
Verification script:
  database/verification/V0001__verify_schemas_and_migration_metadata.sql
Recovery plan:
  Roll forward by correcting defects in a later migration. In a disposable local database only,
  the created objects may be removed manually after confirming no dependent objects exist.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'reference') IS NULL EXEC(N'CREATE SCHEMA [reference] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'market') IS NULL EXEC(N'CREATE SCHEMA [market] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'intelligence') IS NULL EXEC(N'CREATE SCHEMA [intelligence] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'thesis') IS NULL EXEC(N'CREATE SCHEMA [thesis] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'risk') IS NULL EXEC(N'CREATE SCHEMA [risk] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'execution') IS NULL EXEC(N'CREATE SCHEMA [execution] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'portfolio') IS NULL EXEC(N'CREATE SCHEMA [portfolio] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'broker') IS NULL EXEC(N'CREATE SCHEMA [broker] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'ml') IS NULL EXEC(N'CREATE SCHEMA [ml] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'backtest') IS NULL EXEC(N'CREATE SCHEMA [backtest] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'operations') IS NULL EXEC(N'CREATE SCHEMA [operations] AUTHORIZATION [dbo];');
    IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA [audit] AUTHORIZATION [dbo];');

    IF OBJECT_ID(N'[operations].[database_metadata]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[database_metadata]
        (
            [database_metadata_id] tinyint NOT NULL,
            [database_identity] uniqueidentifier NOT NULL,
            [database_name] sysname NOT NULL,
            [schema_baseline_version] varchar(50) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_database_metadata_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_database_metadata_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_database_metadata]
                PRIMARY KEY CLUSTERED ([database_metadata_id]),
            CONSTRAINT [ck_database_metadata_singleton]
                CHECK ([database_metadata_id] = 1),
            CONSTRAINT [uq_database_metadata_identity]
                UNIQUE ([database_identity])
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM [operations].[database_metadata]
        WHERE [database_metadata_id] = 1
    )
    BEGIN
        INSERT INTO [operations].[database_metadata]
        (
            [database_metadata_id],
            [database_identity],
            [database_name],
            [schema_baseline_version],
            [created_by],
            [updated_by]
        )
        VALUES
        (
            1,
            NEWID(),
            DB_NAME(),
            'V0001',
            COALESCE(SUSER_SNAME(), N'UNKNOWN'),
            COALESCE(SUSER_SNAME(), N'UNKNOWN')
        );
    END;

    IF OBJECT_ID(N'[operations].[schema_migrations]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[schema_migrations]
        (
            [schema_migration_id] bigint IDENTITY(1,1) NOT NULL,
            [migration_sequence] int NOT NULL,
            [migration_name] varchar(260) NOT NULL,
            [script_checksum] char(64) NOT NULL,
            [database_identity] uniqueidentifier NOT NULL,
            [environment] varchar(30) NOT NULL,
            [application_version] varchar(100) NULL,
            [applied_at_utc] datetime2(7) NOT NULL,
            [duration_ms] bigint NOT NULL,
            [applied_by] nvarchar(256) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_schema_migrations_created_at_utc] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_schema_migrations]
                PRIMARY KEY CLUSTERED ([schema_migration_id]),
            CONSTRAINT [fk_schema_migrations_database_metadata]
                FOREIGN KEY ([database_identity])
                REFERENCES [operations].[database_metadata] ([database_identity]),
            CONSTRAINT [ck_schema_migrations_sequence]
                CHECK ([migration_sequence] > 0),
            CONSTRAINT [ck_schema_migrations_checksum]
                CHECK ([script_checksum] NOT LIKE '%[^0-9A-Fa-f]%'),
            CONSTRAINT [ck_schema_migrations_environment]
                CHECK ([environment] IN ('LOCAL', 'DEVELOPMENT', 'TEST', 'PAPER', 'SHADOW', 'RESTRICTED_LIVE', 'LIVE')),
            CONSTRAINT [ck_schema_migrations_duration_ms]
                CHECK ([duration_ms] >= 0)
        );

        CREATE UNIQUE INDEX [ux_schema_migrations_sequence]
            ON [operations].[schema_migrations] ([migration_sequence]);

        CREATE UNIQUE INDEX [ux_schema_migrations_name]
            ON [operations].[schema_migrations] ([migration_name]);

        CREATE INDEX [ix_schema_migrations_applied_at_utc]
            ON [operations].[schema_migrations] ([applied_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[operations].[migration_runs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[migration_runs]
        (
            [migration_run_id] bigint IDENTITY(1,1) NOT NULL,
            [migration_sequence] int NOT NULL,
            [migration_name] varchar(260) NOT NULL,
            [script_checksum] char(64) NOT NULL,
            [database_identity] uniqueidentifier NOT NULL,
            [environment] varchar(30) NOT NULL,
            [application_version] varchar(100) NULL,
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [duration_ms] bigint NULL,
            [outcome] varchar(20) NOT NULL,
            [executed_by] nvarchar(256) NOT NULL,
            [error_number] int NULL,
            [error_message] nvarchar(4000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_migration_runs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_migration_runs]
                PRIMARY KEY CLUSTERED ([migration_run_id]),
            CONSTRAINT [fk_migration_runs_database_metadata]
                FOREIGN KEY ([database_identity])
                REFERENCES [operations].[database_metadata] ([database_identity]),
            CONSTRAINT [ck_migration_runs_sequence]
                CHECK ([migration_sequence] > 0),
            CONSTRAINT [ck_migration_runs_checksum]
                CHECK ([script_checksum] NOT LIKE '%[^0-9A-Fa-f]%'),
            CONSTRAINT [ck_migration_runs_environment]
                CHECK ([environment] IN ('LOCAL', 'DEVELOPMENT', 'TEST', 'PAPER', 'SHADOW', 'RESTRICTED_LIVE', 'LIVE')),
            CONSTRAINT [ck_migration_runs_outcome]
                CHECK ([outcome] IN ('STARTED', 'SUCCEEDED', 'FAILED', 'SKIPPED')),
            CONSTRAINT [ck_migration_runs_duration_ms]
                CHECK ([duration_ms] IS NULL OR [duration_ms] >= 0),
            CONSTRAINT [ck_migration_runs_completion]
                CHECK
                (
                    ([outcome] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([outcome] <> 'STARTED' AND [completed_at_utc] IS NOT NULL)
                )
        );

        CREATE INDEX [ix_migration_runs_migration_started]
            ON [operations].[migration_runs]
            (
                [migration_sequence],
                [started_at_utc] DESC
            );

        CREATE INDEX [ix_migration_runs_outcome_started]
            ON [operations].[migration_runs]
            (
                [outcome],
                [started_at_utc] DESC
            );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
