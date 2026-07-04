/*
Migration: V0029__bridge_candidate_thesis_to_trade_plans.sql
Purpose:
  Allow automatic signal-risk trade plans to preserve the authoritative candidate-thesis UID
  carried by intelligence.signal_fusion_lineage without fabricating a legacy thesis.theses row.
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

    IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
        THROW 59901, 'V0006 is required: risk.trade_plans does not exist.', 1;
    IF OBJECT_ID(N'[intelligence].[signal_fusion_lineage]', N'U') IS NULL
        THROW 59902, 'Canonical fusion-signal lineage does not exist.', 1;
    IF COL_LENGTH(N'risk.trade_plans', N'signal_risk_evaluation_id') IS NULL
        THROW 59903, 'V0022 is required: signal-risk trade-plan lineage does not exist.', 1;

    IF COL_LENGTH(N'risk.trade_plans', N'candidate_thesis_uid') IS NULL
    BEGIN
        ALTER TABLE [risk].[trade_plans]
            ADD [candidate_thesis_uid] uniqueidentifier NULL;

        ALTER TABLE [risk].[trade_plans]
            DROP CONSTRAINT [fk_trade_plans_thesis];

        ALTER TABLE [risk].[trade_plans]
            ALTER COLUMN [thesis_id] bigint NULL;

        ALTER TABLE [risk].[trade_plans]
            ADD CONSTRAINT [fk_trade_plans_thesis]
                FOREIGN KEY ([thesis_id], [instrument_id])
                REFERENCES [thesis].[theses] ([thesis_id], [instrument_id]);

        ALTER TABLE [risk].[trade_plans]
            ADD CONSTRAINT [ck_trade_plans_thesis_lineage]
                CHECK
                (
                    ([thesis_id] IS NOT NULL AND [candidate_thesis_uid] IS NULL)
                    OR
                    ([thesis_id] IS NULL AND [candidate_thesis_uid] IS NOT NULL)
                );

        CREATE UNIQUE INDEX [ux_trade_plans_candidate_thesis_version]
            ON [risk].[trade_plans] ([candidate_thesis_uid], [plan_version])
            WHERE [candidate_thesis_uid] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_trade_plans_current_candidate_thesis]
            ON [risk].[trade_plans] ([candidate_thesis_uid])
            WHERE [candidate_thesis_uid] IS NOT NULL AND [is_current] = 1;

        CREATE INDEX [ix_trade_plans_legacy_thesis]
            ON [risk].[trade_plans] ([thesis_id], [is_current], [generated_at_utc] DESC)
            WHERE [thesis_id] IS NOT NULL;
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0029',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
