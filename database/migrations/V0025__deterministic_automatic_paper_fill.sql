/*
Migration: V0025__deterministic_automatic_paper_fill.sql
Purpose:
  Add the durable leased queue that advances authoritative ACKNOWLEDGED PAPER orders
  to deterministic full fills using canonical closed candle evidence.
Authority boundary:
  No external broker, LIVE, portfolio, position, cash, margin or P&L mutation is introduced.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[execution].[orders]', N'U') IS NULL
        THROW 59501, 'V0007 is required: execution.orders does not exist.', 1;
    IF OBJECT_ID(N'[execution].[fills]', N'U') IS NULL
        THROW 59502, 'V0007 is required: execution.fills does not exist.', 1;
    IF OBJECT_ID(N'[execution].[paper_submission_work_items]', N'U') IS NULL
        THROW 59503, 'V0024 is required: execution.paper_submission_work_items does not exist.', 1;

    IF OBJECT_ID(N'[execution].[paper_fill_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[paper_fill_work_items]
        (
            [paper_fill_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [order_id] bigint NOT NULL,
            [order_uid] uniqueidentifier NOT NULL,
            [execution_command_id] bigint NOT NULL,
            [execution_command_uid] uniqueidentifier NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [provider_instrument_key] varchar(200) NOT NULL,
            [eligible_after_utc] datetime2(7) NOT NULL,
            [fill_policy_version] varchar(50) NOT NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [evaluation_count] int NOT NULL
                CONSTRAINT [df_paper_fill_evaluation_count] DEFAULT (0),
            [error_count] int NOT NULL
                CONSTRAINT [df_paper_fill_error_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [last_evaluated_candle_id] bigint NULL,
            [last_evaluated_candle_uid] uniqueidentifier NULL,
            [last_evaluated_close_at_utc] datetime2(7) NULL,
            [fill_event_uid] uniqueidentifier NULL,
            [fill_uid] uniqueidentifier NULL,
            [fill_price] decimal(19,6) NULL,
            [expire_event_uid] uniqueidentifier NULL,
            [reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_paper_fill_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_fill_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_fill_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_paper_fill_work_items]
                PRIMARY KEY CLUSTERED ([paper_fill_work_item_id]),
            CONSTRAINT [uq_paper_fill_order_id]
                UNIQUE ([order_id]),
            CONSTRAINT [uq_paper_fill_order_uid]
                UNIQUE ([order_uid]),
            CONSTRAINT [uq_paper_fill_execution_command]
                UNIQUE ([execution_command_id]),
            CONSTRAINT [fk_paper_fill_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [fk_paper_fill_execution_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [fk_paper_fill_last_candle]
                FOREIGN KEY ([last_evaluated_candle_id])
                REFERENCES [market].[candles] ([candle_id]),
            CONSTRAINT [ck_paper_fill_payload_json]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_paper_fill_reasons_json]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_paper_fill_counts]
                CHECK ([evaluation_count] >= 0 AND [error_count] >= 0),
            CONSTRAINT [ck_paper_fill_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'PENDING', 'LEASED', 'FILLED', 'DEFERRED', 'RETRY_PENDING',
                        'EXPIRED', 'REJECTED', 'FAILED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_paper_fill_lease]
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
            CONSTRAINT [ck_paper_fill_last_candle]
                CHECK
                (
                    ([last_evaluated_candle_id] IS NULL
                        AND [last_evaluated_candle_uid] IS NULL
                        AND [last_evaluated_close_at_utc] IS NULL)
                    OR
                    ([last_evaluated_candle_id] IS NOT NULL
                        AND [last_evaluated_candle_uid] IS NOT NULL
                        AND [last_evaluated_close_at_utc] IS NOT NULL)
                ),
            CONSTRAINT [ck_paper_fill_result]
                CHECK
                (
                    ([current_status] = 'FILLED'
                        AND [fill_event_uid] IS NOT NULL
                        AND [fill_uid] IS NOT NULL
                        AND [fill_price] > 0
                        AND [expire_event_uid] IS NULL)
                    OR
                    ([current_status] = 'EXPIRED'
                        AND [fill_event_uid] IS NULL
                        AND [fill_uid] IS NULL
                        AND [fill_price] IS NULL
                        AND [expire_event_uid] IS NOT NULL)
                    OR
                    ([current_status] NOT IN ('FILLED','EXPIRED')
                        AND [fill_event_uid] IS NULL
                        AND [fill_uid] IS NULL
                        AND [fill_price] IS NULL
                        AND [expire_event_uid] IS NULL)
                )
        );

        CREATE UNIQUE INDEX [ux_paper_fill_event_uid]
            ON [execution].[paper_fill_work_items] ([fill_event_uid])
            WHERE [fill_event_uid] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_paper_fill_uid]
            ON [execution].[paper_fill_work_items] ([fill_uid])
            WHERE [fill_uid] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_paper_fill_expire_event_uid]
            ON [execution].[paper_fill_work_items] ([expire_event_uid])
            WHERE [expire_event_uid] IS NOT NULL;

        CREATE INDEX [ix_paper_fill_available]
            ON [execution].[paper_fill_work_items]
                ([current_status], [next_attempt_at_utc], [paper_fill_work_item_id])
            INCLUDE ([evaluation_count], [error_count], [order_id], [order_uid]);

        CREATE INDEX [ix_paper_fill_lease]
            ON [execution].[paper_fill_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0025',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
