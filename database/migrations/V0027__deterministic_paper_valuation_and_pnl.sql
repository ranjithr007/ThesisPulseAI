/*
Migration: V0027__deterministic_paper_valuation_and_pnl.sql
Purpose:
  Add the durable leased queue for deterministic point-in-time PAPER portfolio
  valuation using canonical closed-candle evidence.
Authority boundary:
  Portfolio Service owns valuation marks, position valuations and P&L snapshots.
  No LIVE, broker, margin, settlement, risk-limit or exit-order authority is introduced.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[portfolio].[portfolios]', N'U') IS NULL
        THROW 59701, 'V0008 is required: portfolio.portfolios does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[valuation_marks]', N'U') IS NULL
        THROW 59702, 'V0008 is required: portfolio.valuation_marks does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[position_valuations]', N'U') IS NULL
        THROW 59703, 'V0008 is required: portfolio.position_valuations does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[pnl_snapshots]', N'U') IS NULL
        THROW 59704, 'V0008 is required: portfolio.pnl_snapshots does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[fill_projection_work_items]', N'U') IS NULL
        THROW 59705, 'V0026 is required: portfolio.fill_projection_work_items does not exist.', 1;

    IF OBJECT_ID(N'[portfolio].[valuation_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[valuation_work_items]
        (
            [valuation_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [request_uid] uniqueidentifier NOT NULL,
            [snapshot_uid] uniqueidentifier NOT NULL,
            [portfolio_id] bigint NOT NULL,
            [portfolio_uid] uniqueidentifier NOT NULL,
            [portfolio_code] varchar(100) NOT NULL,
            [as_of_utc] datetime2(7) NOT NULL,
            [valuation_policy_version] varchar(100) NOT NULL,
            [position_fingerprint] varchar(2000) NOT NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_valuation_work_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [result_status] varchar(30) NULL,
            [pnl_snapshot_id] bigint NULL,
            [reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_valuation_work_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_valuation_work_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_valuation_work_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_valuation_work_items]
                PRIMARY KEY CLUSTERED ([valuation_work_item_id]),
            CONSTRAINT [uq_valuation_work_request]
                UNIQUE ([request_uid]),
            CONSTRAINT [uq_valuation_work_snapshot]
                UNIQUE ([snapshot_uid]),
            CONSTRAINT [uq_valuation_work_point]
                UNIQUE
                ([portfolio_id], [as_of_utc], [valuation_policy_version], [position_fingerprint]),
            CONSTRAINT [fk_valuation_work_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [fk_valuation_work_snapshot]
                FOREIGN KEY ([pnl_snapshot_id])
                REFERENCES [portfolio].[pnl_snapshots] ([pnl_snapshot_id]),
            CONSTRAINT [ck_valuation_work_payload_json]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_valuation_work_reasons_json]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_valuation_work_attempt_count]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_valuation_work_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'PENDING', 'LEASED', 'VALUED', 'DUPLICATE',
                        'RETRY_PENDING', 'REJECTED', 'FAILED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_valuation_work_result]
                CHECK
                (
                    ([current_status] IN ('VALUED', 'DUPLICATE')
                        AND [result_status] = [current_status]
                        AND [pnl_snapshot_id] IS NOT NULL)
                    OR
                    ([current_status] NOT IN ('VALUED', 'DUPLICATE')
                        AND [result_status] IS NULL
                        AND [pnl_snapshot_id] IS NULL)
                ),
            CONSTRAINT [ck_valuation_work_lease]
                CHECK
                (
                    ([current_status] = 'LEASED'
                        AND [lease_owner] IS NOT NULL
                        AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([current_status] <> 'LEASED'
                        AND [lease_owner] IS NULL
                        AND [lease_expires_at_utc] IS NULL)
                )
        );

        CREATE INDEX [ix_valuation_work_available]
            ON [portfolio].[valuation_work_items]
                ([current_status], [next_attempt_at_utc], [valuation_work_item_id])
            INCLUDE ([attempt_count], [portfolio_id], [as_of_utc]);

        CREATE INDEX [ix_valuation_work_lease]
            ON [portfolio].[valuation_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';

        CREATE INDEX [ix_valuation_work_portfolio]
            ON [portfolio].[valuation_work_items]
                ([portfolio_id], [as_of_utc] DESC, [current_status]);
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0027',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
