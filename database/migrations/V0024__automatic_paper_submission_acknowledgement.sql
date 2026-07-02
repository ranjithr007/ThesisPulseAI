/*
Migration: V0024__automatic_paper_submission_acknowledgement.sql
Purpose:
  Add the durable leased queue that advances authoritative PAPER orders from CREATED
  through deterministic internal submission to ACKNOWLEDGED.
Authority boundary:
  No external broker, Upstox, fill, portfolio or LIVE side effect is introduced.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[execution].[orders]', N'U') IS NULL
        THROW 59401, 'V0007 is required: execution.orders does not exist.', 1;
    IF OBJECT_ID(N'[execution].[execution_commands]', N'U') IS NULL
        THROW 59402, 'V0007 is required: execution.execution_commands does not exist.', 1;
    IF OBJECT_ID(N'[execution].[execution_command_states]', N'U') IS NULL
        THROW 59403, 'V0007 is required: execution.execution_command_states does not exist.', 1;

    IF OBJECT_ID(N'[execution].[paper_submission_work_items]', N'U') IS NULL
    BEGIN
        CREATE TABLE [execution].[paper_submission_work_items]
        (
            [paper_submission_work_item_id] bigint IDENTITY(1,1) NOT NULL,
            [order_id] bigint NOT NULL,
            [order_uid] uniqueidentifier NOT NULL,
            [execution_command_id] bigint NOT NULL,
            [execution_command_uid] uniqueidentifier NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [valid_until_utc] datetime2(7) NOT NULL,
            [submit_event_uid] uniqueidentifier NOT NULL,
            [acknowledge_event_uid] uniqueidentifier NOT NULL,
            [expire_event_uid] uniqueidentifier NOT NULL,
            [broker_order_id] varchar(200) NOT NULL,
            [current_status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL
                CONSTRAINT [df_paper_submission_attempt_count] DEFAULT (0),
            [next_attempt_at_utc] datetime2(7) NOT NULL,
            [lease_owner] nvarchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [reasons_json] nvarchar(max) NOT NULL
                CONSTRAINT [df_paper_submission_reasons] DEFAULT (N'[]'),
            [last_error] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_submission_created_at] DEFAULT SYSUTCDATETIME(),
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_paper_submission_updated_at] DEFAULT SYSUTCDATETIME(),
            CONSTRAINT [pk_paper_submission_work_items]
                PRIMARY KEY CLUSTERED ([paper_submission_work_item_id]),
            CONSTRAINT [uq_paper_submission_order_id]
                UNIQUE ([order_id]),
            CONSTRAINT [uq_paper_submission_order_uid]
                UNIQUE ([order_uid]),
            CONSTRAINT [uq_paper_submission_execution_command]
                UNIQUE ([execution_command_id]),
            CONSTRAINT [uq_paper_submission_submit_event]
                UNIQUE ([submit_event_uid]),
            CONSTRAINT [uq_paper_submission_ack_event]
                UNIQUE ([acknowledge_event_uid]),
            CONSTRAINT [uq_paper_submission_expire_event]
                UNIQUE ([expire_event_uid]),
            CONSTRAINT [uq_paper_submission_broker_order]
                UNIQUE ([broker_order_id]),
            CONSTRAINT [fk_paper_submission_order]
                FOREIGN KEY ([order_id])
                REFERENCES [execution].[orders] ([order_id]),
            CONSTRAINT [fk_paper_submission_execution_command]
                FOREIGN KEY ([execution_command_id])
                REFERENCES [execution].[execution_commands] ([execution_command_id]),
            CONSTRAINT [ck_paper_submission_reasons_json]
                CHECK (ISJSON([reasons_json]) = 1),
            CONSTRAINT [ck_paper_submission_attempt_count]
                CHECK ([attempt_count] >= 0),
            CONSTRAINT [ck_paper_submission_status]
                CHECK
                (
                    [current_status] IN
                    (
                        'PENDING', 'LEASED', 'ACKNOWLEDGED', 'REJECTED',
                        'RETRY_PENDING', 'EXPIRED', 'FAILED', 'CANCELLED'
                    )
                ),
            CONSTRAINT [ck_paper_submission_lease]
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
            CONSTRAINT [ck_paper_submission_validity]
                CHECK ([valid_until_utc] > [created_at_utc])
        );

        CREATE INDEX [ix_paper_submission_available]
            ON [execution].[paper_submission_work_items]
                ([current_status], [next_attempt_at_utc], [paper_submission_work_item_id])
            INCLUDE ([attempt_count], [order_id], [order_uid], [valid_until_utc]);

        CREATE INDEX [ix_paper_submission_lease]
            ON [execution].[paper_submission_work_items] ([lease_expires_at_utc])
            WHERE [current_status] = 'LEASED';
    END;

    UPDATE [operations].[database_metadata]
    SET [schema_baseline_version] = 'V0024',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
