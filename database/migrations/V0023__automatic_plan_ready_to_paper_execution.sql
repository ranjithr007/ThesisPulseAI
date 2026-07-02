/*
Migration: V0023__automatic_plan_ready_to_paper_execution.sql
Purpose:
  Add the durable leased queue that converts authoritative PAPER PLAN_READY records into
  execution authorization requests and records the resulting execution command and order UIDs.
Authority boundary:
  This migration creates no broker or LIVE submission path. Orders produced by this slice remain CREATED.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[risk].[trade_plans]', N'U') IS NULL
        THROW 59301, 'V0006 and V0022 are required: risk.trade_plans does not exist.', 1;
    IF OBJECT_ID(N'[execution].[execution_commands]', N'U') IS NULL
        THROW 59302, 'V0007 is required: execution.execution_commands does not exist.', 1;
    IF OBJECT_ID(N'[execution].[orders]', N'U') IS NULL
        THROW 59303, 'V0007 is required: execution.orders does not exist.', 1;

    IF OBJECT_ID(N'[execution].[paper_execution_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[paper_execution_work_items]
        (
            [paper_execution_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [trade_plan_id] bigint NOT NULL,
            [trade_plan_uid] uniqueidentifier NOT NULL,
            [source_message_uid] uniqueidentifier NOT NULL,
            [request_uid] uniqueidentifier NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [idempotency_key] varchar(200) NOT NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_paper_execution_work_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [execution_command_uid] uniqueidentifier NULL,
            [order_uid] uniqueidentifier NULL,
            [rejection_reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_paper_execution_work_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_execution_work_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_execution_work_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_paper_execution_work_items]
                PRIMARY KEY CLUSTERED ([paper_execution_work_item_id]),
            CONSTRAINT [uq_paper_execution_work_trade_plan_id]
                UNIQUE ([trade_plan_id]),
            CONSTRAINT [uq_paper_execution_work_trade_plan_uid]
                UNIQUE ([trade_plan_uid]),
            CONSTRAINT [uq_paper_execution_work_source_message]
                UNIQUE ([source_message_uid]),
            CONSTRAINT [uq_paper_execution_work_request]
                UNIQUE ([request_uid]),
            CONSTRAINT [uq_paper_execution_work_idempotency]
                UNIQUE ([idempotency_key]),
            CONSTRAINT [fk_paper_execution_work_trade_plan]
                FOREIGN KEY ([trade_plan_id])
                REFERENCES [risk].[trade_plans] ([trade_plan_id]),
            CONSTRAINT [ck_paper_execution_work_payload_json]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_paper_execution_work_reasons_json]
                CHECK (ISJSON([rejection_reasons_json]) = 1),
            CONSTRAINT [ck_paper_execution_work_attempt_count]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_paper_execution_work_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'PENDING', 'LEASED', 'AUTHORIZED', 'REJECTED',
                        'RETRY_PENDING', 'EXPIRED', 'FAILED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_paper_execution_work_lease]
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
            CONSTRAINT [ck_paper_execution_work_authorized_result]
                CHECK
                (
                    ([current_status] = 'AUTHORIZED'
                        AND [execution_command_uid] IS NOT NULL
                        AND [order_uid] IS NOT NULL)
                    OR
                    ([current_status] <> 'AUTHORIZED'
                        AND [execution_command_uid] IS NULL
                        AND [order_uid] IS NULL)
                )
        );

        CREATE UNIQUE INDEX [ux_paper_execution_work_command_uid]
            ON [execution].[paper_execution_work_items] ([execution_command_uid])
            WHERE [execution_command_uid] IS NOT NULL;

        CREATE UNIQUE INDEX [ux_paper_execution_work_order_uid]
            ON [execution].[paper_execution_work_items] ([order_uid])
            WHERE [order_uid] IS NOT NULL;

        CREATE INDEX [ix_paper_execution_work_available]
            ON [execution].[paper_execution_work_items]
                ([current_status], [next_attempt_at_utc], [paper_execution_work_item_id])
            INCLUDE ([attempt_count], [trade_plan_id], [trade_plan_uid]);

        CREATE INDEX [ix_paper_execution_work_lease]
            ON [execution].[paper_execution_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0023',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
