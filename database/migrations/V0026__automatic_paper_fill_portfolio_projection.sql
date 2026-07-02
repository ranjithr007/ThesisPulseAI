/*
Migration: V0026__automatic_paper_fill_portfolio_projection.sql
Purpose:
  Add the durable leased queue that projects authoritative PAPER fills into the
  matching Portfolio Service ledger exactly once in effect.
Authority boundary:
  Portfolio Service remains the only position, lot, cash and realized-PnL authority.
  No LIVE, broker, valuation, margin or automatic correction authority is introduced.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[execution].[fills]', N'U') IS NULL
        THROW 59601, 'V0007 is required: execution.fills does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[portfolios]', N'U') IS NULL
        THROW 59602, 'V0008 is required: portfolio.portfolios does not exist.', 1;
    IF OBJECT_ID(N'[portfolio].[position_events]', N'U') IS NULL
        THROW 59603, 'V0008 is required: portfolio.position_events does not exist.', 1;
    IF OBJECT_ID(N'[execution].[paper_fill_work_items]', N'U') IS NULL
        THROW 59604, 'V0025 is required: execution.paper_fill_work_items does not exist.', 1;

    IF OBJECT_ID(N'[portfolio].[fill_projection_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [portfolio].[fill_projection_work_items]
        (
            [fill_projection_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [fill_id] bigint NOT NULL,
            [fill_uid] uniqueidentifier NOT NULL,
            [portfolio_id] bigint NULL,
            [portfolio_code] varchar(100) NULL,
            [projection_request_uid] uniqueidentifier NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [fill_at_utc] datetime2(7) NOT NULL,
            [projection_policy_version] varchar(100) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_fill_projection_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [projection_result_status] varchar(30) NULL,
            [position_uid] uniqueidentifier NULL,
            [reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_fill_projection_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_fill_projection_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_fill_projection_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_fill_projection_work_items]
                PRIMARY KEY CLUSTERED ([fill_projection_work_item_id]),
            CONSTRAINT [uq_fill_projection_fill_id]
                UNIQUE ([fill_id]),
            CONSTRAINT [uq_fill_projection_fill_uid]
                UNIQUE ([fill_uid]),
            CONSTRAINT [uq_fill_projection_request_uid]
                UNIQUE ([projection_request_uid]),
            CONSTRAINT [fk_fill_projection_fill]
                FOREIGN KEY ([fill_id])
                REFERENCES [execution].[fills] ([fill_id]),
            CONSTRAINT [fk_fill_projection_portfolio]
                FOREIGN KEY ([portfolio_id])
                REFERENCES [portfolio].[portfolios] ([portfolio_id]),
            CONSTRAINT [ck_fill_projection_attempt_count]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_fill_projection_reasons_json]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_fill_projection_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'PENDING', 'LEASED', 'PROJECTED', 'DUPLICATE',
                        'RETRY_PENDING', 'REJECTED', 'FAILED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_fill_projection_result_status]
                CHECK
                (
                    [projection_result_status] IS NULL
                    OR [projection_result_status] IN ('PROJECTED', 'DUPLICATE')
                ),
            CONSTRAINT [ck_fill_projection_routing]
                CHECK
                (
                    ([current_status] IN ('PENDING', 'LEASED', 'PROJECTED', 'DUPLICATE', 'RETRY_PENDING')
                        AND [portfolio_id] IS NOT NULL
                        AND [portfolio_code] IS NOT NULL)
                    OR
                    ([current_status] IN ('REJECTED', 'FAILED', 'CANCELLED'))
                ),
            CONSTRAINT [ck_fill_projection_lease]
                CHECK
                (
                    ([current_status] = 'LEASED'
                        AND [lease_owner] IS NOT NULL
                        AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([current_status] <> 'LEASED'
                        AND [lease_owner] IS NULL
                        AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_fill_projection_terminal_result]
                CHECK
                (
                    ([current_status] IN ('PROJECTED', 'DUPLICATE')
                        AND [projection_result_status] = [current_status]
                        AND [position_uid] IS NOT NULL)
                    OR
                    ([current_status] NOT IN ('PROJECTED', 'DUPLICATE')
                        AND [projection_result_status] IS NULL
                        AND [position_uid] IS NULL)
                )
        );

        CREATE INDEX [ix_fill_projection_available]
            ON [portfolio].[fill_projection_work_items]
                ([current_status], [next_attempt_at_utc], [fill_projection_work_item_id])
            INCLUDE ([attempt_count], [fill_id], [fill_uid], [portfolio_id]);

        CREATE INDEX [ix_fill_projection_lease]
            ON [portfolio].[fill_projection_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';

        CREATE INDEX [ix_fill_projection_portfolio]
            ON [portfolio].[fill_projection_work_items]
                ([portfolio_id], [current_status], [fill_at_utc]);
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0026',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
