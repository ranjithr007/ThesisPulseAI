/*
Migration: V0018__partition_option_chain_outputs_by_expiry.sql
Purpose:
  Preserve independent current/revision lineage when multiple option expiries
  share the same underlying and observation timestamp.
Dependencies:
  V0017__create_option_chain_intelligence_output_tables.sql
Backward compatibility:
  Existing non-option engine outputs retain their original uniqueness behavior
  because their computed partition key remains NULL. Existing index names are
  preserved so earlier verification scripts remain valid.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[intelligence].[engine_outputs]', N'U') IS NULL
        THROW 61801, 'V0004 intelligence.engine_outputs is required.', 1;

    IF COL_LENGTH(N'[intelligence].[engine_outputs]', N'output_partition_key') IS NULL
    BEGIN
        ALTER TABLE [intelligence].[engine_outputs]
            ADD [output_partition_key] AS
            (
                CASE
                    WHEN [timeframe] = 'OPTION_CHAIN'
                    THEN CONVERT
                    (
                        varchar(100),
                        JSON_VALUE([raw_contract_json], '$.expiryMetrics[0].expiryDate')
                    )
                    ELSE NULL
                END
            ) PERSISTED;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.key_constraints
        WHERE [name] = N'uq_engine_outputs_revision'
          AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
    )
    BEGIN
        ALTER TABLE [intelligence].[engine_outputs]
            DROP CONSTRAINT [uq_engine_outputs_revision];
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'uq_engine_outputs_revision'
          AND [object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
    )
    BEGIN
        DROP INDEX [uq_engine_outputs_revision]
            ON [intelligence].[engine_outputs];
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'ux_engine_outputs_current'
          AND [object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
    )
    BEGIN
        DROP INDEX [ux_engine_outputs_current]
            ON [intelligence].[engine_outputs];
    END;

    CREATE UNIQUE INDEX [uq_engine_outputs_revision]
        ON [intelligence].[engine_outputs]
        ([engine_id], [instrument_id], [timeframe], [as_of_utc],
         [output_partition_key], [revision]);

    CREATE UNIQUE INDEX [ux_engine_outputs_current]
        ON [intelligence].[engine_outputs]
        ([engine_id], [instrument_id], [timeframe], [as_of_utc],
         [output_partition_key])
        WHERE [is_current] = 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE [name] = N'ck_engine_outputs_option_chain_partition'
          AND [parent_object_id] = OBJECT_ID(N'[intelligence].[engine_outputs]')
    )
    BEGIN
        ALTER TABLE [intelligence].[engine_outputs] WITH CHECK
            ADD CONSTRAINT [ck_engine_outputs_option_chain_partition]
            CHECK
            (
                [timeframe] <> 'OPTION_CHAIN'
                OR [output_partition_key] IS NOT NULL
            );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
