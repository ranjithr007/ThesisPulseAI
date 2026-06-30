SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'[intelligence].[engine_output_dependencies]', N'U') IS NULL
    THROW 62301, 'Required engine output dependency table is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [name] = N'uq_engine_output_dependencies'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_output_dependencies]')
)
    THROW 62302, 'Required dependency uniqueness rule is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = N'fk_engine_output_dependencies_downstream'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_output_dependencies]')
)
    THROW 62303, 'Required downstream dependency reference is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = N'fk_engine_output_dependencies_upstream'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_output_dependencies]')
)
    THROW 62304, 'Required upstream dependency reference is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [name] = N'ck_engine_output_dependencies_not_self'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_output_dependencies]')
)
    THROW 62305, 'Required self-reference guard is missing.', 1;

PRINT 'Engine output dependency verification passed.';
