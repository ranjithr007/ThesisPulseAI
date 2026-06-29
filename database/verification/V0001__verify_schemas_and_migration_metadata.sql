/*
Verification: V0001__verify_schemas_and_migration_metadata.sql
Purpose: Verify the business schemas, migration metadata tables, singleton metadata row,
         foreign keys, and required indexes created by V0001.
Expected result: One PASS result set and no raised verification error.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @expected_schemas TABLE ([schema_name] sysname NOT NULL PRIMARY KEY);
INSERT INTO @expected_schemas ([schema_name])
VALUES
    (N'reference'),
    (N'market'),
    (N'intelligence'),
    (N'thesis'),
    (N'risk'),
    (N'execution'),
    (N'portfolio'),
    (N'broker'),
    (N'ml'),
    (N'backtest'),
    (N'operations'),
    (N'audit');

IF EXISTS
(
    SELECT 1
    FROM @expected_schemas AS expected
    WHERE SCHEMA_ID(expected.[schema_name]) IS NULL
)
BEGIN
    SELECT expected.[schema_name] AS [missing_schema]
    FROM @expected_schemas AS expected
    WHERE SCHEMA_ID(expected.[schema_name]) IS NULL;

    RAISERROR('V0001 schema verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_tables TABLE
(
    [schema_name] sysname NOT NULL,
    [table_name] sysname NOT NULL,
    PRIMARY KEY ([schema_name], [table_name])
);

INSERT INTO @expected_tables ([schema_name], [table_name])
VALUES
    (N'operations', N'database_metadata'),
    (N'operations', N'schema_migrations'),
    (N'operations', N'migration_runs');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL
)
BEGIN
    SELECT
        expected.[schema_name],
        expected.[table_name]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL;

    RAISERROR('V0001 table verification failed.', 16, 1);
    RETURN;
END;

IF (SELECT COUNT_BIG(*) FROM [operations].[database_metadata]) <> 1
BEGIN
    RAISERROR('operations.database_metadata must contain exactly one row.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [database_identity] IS NOT NULL
      AND [database_name] = DB_NAME()
      AND [schema_baseline_version] = 'V0001'
)
BEGIN
    RAISERROR('The V0001 database metadata row is invalid.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'[operations].[schema_migrations]')
      AND [name] = N'fk_schema_migrations_database_metadata'
)
BEGIN
    RAISERROR('Missing schema migration metadata foreign key.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [parent_object_id] = OBJECT_ID(N'[operations].[migration_runs]')
      AND [name] = N'fk_migration_runs_database_metadata'
)
BEGIN
    RAISERROR('Missing migration run metadata foreign key.', 16, 1);
    RETURN;
END;

DECLARE @expected_indexes TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [index_name] sysname NOT NULL,
    [must_be_unique] bit NOT NULL,
    PRIMARY KEY ([table_name], [index_name])
);

INSERT INTO @expected_indexes ([table_name], [index_name], [must_be_unique])
VALUES
    (N'[operations].[schema_migrations]', N'ux_schema_migrations_sequence', 1),
    (N'[operations].[schema_migrations]', N'ux_schema_migrations_name', 1),
    (N'[operations].[schema_migrations]', N'ix_schema_migrations_applied_at_utc', 0),
    (N'[operations].[migration_runs]', N'ix_migration_runs_migration_started', 0),
    (N'[operations].[migration_runs]', N'ix_migration_runs_outcome_started', 0);

IF EXISTS
(
    SELECT 1
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
    )
)
BEGIN
    SELECT
        expected.[table_name],
        expected.[index_name]
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
    );

    RAISERROR('V0001 index verification failed.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0001' AS [migration_version],
    DB_NAME() AS [database_name],
    [database_identity],
    [schema_baseline_version],
    [created_at_utc],
    [updated_at_utc]
FROM [operations].[database_metadata]
WHERE [database_metadata_id] = 1;
