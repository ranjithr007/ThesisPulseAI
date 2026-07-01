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
    FROM sys.indexes
    WHERE [name] = N'ux_engine_outputs_revision_partitioned'
      AND [object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND [is_unique] = 1
)
    THROW 61853, 'Partitioned engine-output revision index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'ux_engine_outputs_current_partitioned'
      AND [object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
      AND [is_unique] = 1
      AND [has_filter] = 1
)
    THROW 61854, 'Partitioned current engine-output index is missing.', 1;

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE [name] = N'ux_engine_outputs_current'
      AND [object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
)
    THROW 61855, 'Legacy unpartitioned current-output index still exists.', 1;

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
