SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'[intelligence].[engine_outputs]', N'output_partition_key') IS NULL
    THROW 61851, 'engine_outputs.output_partition_key is missing.', 1;

IF COLUMNPROPERTY
(
    OBJECT_ID(N'[intelligence].[engine_outputs]'),
    N'output_partition_key',
    'IsComputed'
) <> 1
    THROW 61852, 'engine_outputs.output_partition_key must be computed.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS index_definition
    INNER JOIN sys.index_columns AS index_column
        ON index_column.[object_id] = index_definition.[object_id]
       AND index_column.[index_id] = index_definition.[index_id]
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = index_column.[object_id]
       AND column_definition.[column_id] = index_column.[column_id]
    WHERE index_definition.[name] = N'uq_engine_outputs_revision'
      AND index_definition.[object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND index_definition.[is_unique] = 1
      AND column_definition.[name] = N'output_partition_key'
      AND index_column.[is_included_column] = 0
)
    THROW 61853, 'Expiry-partitioned engine-output revision index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS index_definition
    INNER JOIN sys.index_columns AS index_column
        ON index_column.[object_id] = index_definition.[object_id]
       AND index_column.[index_id] = index_definition.[index_id]
    INNER JOIN sys.columns AS column_definition
        ON column_definition.[object_id] = index_column.[object_id]
       AND column_definition.[column_id] = index_column.[column_id]
    WHERE index_definition.[name] = N'ux_engine_outputs_current'
      AND index_definition.[object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND index_definition.[is_unique] = 1
      AND index_definition.[has_filter] = 1
      AND column_definition.[name] = N'output_partition_key'
      AND index_column.[is_included_column] = 0
)
    THROW 61854, 'Expiry-partitioned current engine-output index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE [name] = N'ck_engine_outputs_option_chain_partition'
      AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND [is_disabled] = 0
      AND [is_not_trusted] = 0
)
    THROW 61856, 'Option-chain output partition constraint is missing or untrusted.', 1;

PRINT 'PASS V0018 option-chain output partitioning';
GO
