/*
Migration: V0016__add_option_chain_snapshot_warning_lineage.sql
Purpose:
  Persist deterministic normalization and lineage warnings with each immutable
  option-chain snapshot so later point-in-time reads preserve rejection context.
Dependencies:
  V0015__create_derivatives_reference_and_market_data_tables.sql
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

    IF OBJECT_ID(N'[market].[option_chain_snapshots]', N'U') IS NULL
        THROW 61601, 'V0015 market.option_chain_snapshots is required.', 1;

    IF COL_LENGTH(N'[market].[option_chain_snapshots]', N'warnings_json') IS NULL
    BEGIN
        ALTER TABLE [market].[option_chain_snapshots]
            ADD [warnings_json] nvarchar(max) NULL;

        ALTER TABLE [market].[option_chain_snapshots]
            ADD CONSTRAINT [ck_option_chain_snapshots_warnings_json]
            CHECK ([warnings_json] IS NULL OR ISJSON([warnings_json]) = 1);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
