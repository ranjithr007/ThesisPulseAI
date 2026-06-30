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

IF SCHEMA_ID(N'operations') IS NULL
    THROW 61201, 'V0001 operations schema is required.', 1;

IF OBJECT_ID(N'[operations].[outbox_messages]', N'U') IS NULL
    THROW 61202, 'V0009 operations.outbox_messages is required.', 1;

IF OBJECT_ID(N'[operations].[paper_workflows]', N'U') IS NULL
BEGIN
    CREATE TABLE [operations].[paper_workflows]
    (
        [paper_workflow_id] bigint IDENTITY(1,1) NOT NULL,
        [paper_workflow_uid] uniqueidentifier NOT NULL,
        [request_uid] uniqueidentifier NOT NULL,
        [source_message_uid] uniqueidentifier NOT NULL,
        [environment] varchar(20) NOT NULL,
        [idempotency_key] varchar(200) NOT NULL,
        [correlation_id] uniqueidentifier NOT NULL,
        [instrument_key] varchar(200) NOT NULL,
        [primary_timeframe] varchar(20) NOT NULL,
        [status] varchar(30) NOT NULL,
        [current_step] varchar(60) NULL,
        [attempt_count] int NOT NULL CONSTRAINT [df_paper_workflows_attempt_count] DEFAULT (0),
        [request_json] nvarchar(max) NOT NULL,
        [result_json] nvarchar(max) NULL,
        [next_attempt_at_utc] datetime2(7) NULL,
        [started_at_utc] datetime2(7) NOT NULL,
        [completed_at_utc] datetime2(7) NULL,
        [last_error_code] varchar(100) NULL,
        [last_error_message] nvarchar(2000) NULL,
        [created_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_paper_workflows_created_at] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [updated_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_paper_workflows_updated_at] DEFAULT SYSUTCDATETIME(),
        [updated_by] nvarchar(256) NOT NULL,
        [row_version] rowversion NOT NULL,
        CONSTRAINT [pk_paper_workflows] PRIMARY KEY CLUSTERED ([paper_workflow_id]),
        CONSTRAINT [uq_paper_workflows_uid] UNIQUE ([paper_workflow_uid]),
        CONSTRAINT [uq_paper_workflows_request] UNIQUE ([request_uid]),
        CONSTRAINT [uq_paper_workflows_idempotency] UNIQUE ([environment], [idempotency_key]),
        CONSTRAINT [ck_paper_workflows_environment] CHECK ([environment] = 'PAPER'),
        CONSTRAINT [ck_paper_workflows_status] CHECK ([status] IN ('RUNNING','RETRY_PENDING','COMPLETED','REJECTED','FAILED')),
        CONSTRAINT [ck_paper_workflows_attempts] CHECK ([attempt_count] >= 0),
        CONSTRAINT [ck_paper_workflows_request_json] CHECK (ISJSON([request_json]) = 1),
        CONSTRAINT [ck_paper_workflows_result_json] CHECK ([result_json] IS NULL OR ISJSON([result_json]) = 1),
        CONSTRAINT [ck_paper_workflows_completion] CHECK
        (
            ([status] IN ('COMPLETED','REJECTED','FAILED') AND [completed_at_utc] IS NOT NULL)
            OR ([status] IN ('RUNNING','RETRY_PENDING') AND [completed_at_utc] IS NULL)
        ),
        CONSTRAINT [ck_paper_workflows_retry] CHECK
        (
            ([status] = 'RETRY_PENDING' AND [next_attempt_at_utc] IS NOT NULL)
            OR ([status] <> 'RETRY_PENDING' AND [next_attempt_at_utc] IS NULL)
        )
    );

    CREATE INDEX [ix_paper_workflows_resume]
        ON [operations].[paper_workflows] ([status],[next_attempt_at_utc],[paper_workflow_id])
        INCLUDE ([paper_workflow_uid],[attempt_count],[current_step]);
END;

IF OBJECT_ID(N'[operations].[paper_workflow_steps]', N'U') IS NULL
BEGIN
    CREATE TABLE [operations].[paper_workflow_steps]
    (
        [paper_workflow_step_id] bigint IDENTITY(1,1) NOT NULL,
        [paper_workflow_step_uid] uniqueidentifier NOT NULL,
        [paper_workflow_id] bigint NOT NULL,
        [step_code] varchar(60) NOT NULL,
        [step_sequence] int NOT NULL,
        [status] varchar(20) NOT NULL,
        [attempt_count] int NOT NULL CONSTRAINT [df_paper_workflow_steps_attempt_count] DEFAULT (0),
        [request_json] nvarchar(max) NULL,
        [response_json] nvarchar(max) NULL,
        [output_reference] varchar(300) NULL,
        [retryable] bit NOT NULL CONSTRAINT [df_paper_workflow_steps_retryable] DEFAULT (0),
        [started_at_utc] datetime2(7) NULL,
        [completed_at_utc] datetime2(7) NULL,
        [error_code] varchar(100) NULL,
        [error_message] nvarchar(2000) NULL,
        [created_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_paper_workflow_steps_created_at] DEFAULT SYSUTCDATETIME(),
        [created_by] nvarchar(256) NOT NULL,
        [updated_at_utc] datetime2(7) NOT NULL CONSTRAINT [df_paper_workflow_steps_updated_at] DEFAULT SYSUTCDATETIME(),
        [updated_by] nvarchar(256) NOT NULL,
        [row_version] rowversion NOT NULL,
        CONSTRAINT [pk_paper_workflow_steps] PRIMARY KEY CLUSTERED ([paper_workflow_step_id]),
        CONSTRAINT [uq_paper_workflow_steps_uid] UNIQUE ([paper_workflow_step_uid]),
        CONSTRAINT [uq_paper_workflow_steps_code] UNIQUE ([paper_workflow_id],[step_code]),
        CONSTRAINT [uq_paper_workflow_steps_sequence] UNIQUE ([paper_workflow_id],[step_sequence]),
        CONSTRAINT [fk_paper_workflow_steps_workflow] FOREIGN KEY ([paper_workflow_id]) REFERENCES [operations].[paper_workflows] ([paper_workflow_id]),
        CONSTRAINT [ck_paper_workflow_steps_sequence] CHECK ([step_sequence] >= 1),
        CONSTRAINT [ck_paper_workflow_steps_attempts] CHECK ([attempt_count] >= 0),
        CONSTRAINT [ck_paper_workflow_steps_status] CHECK ([status] IN ('PENDING','RUNNING','SUCCEEDED','REJECTED','FAILED')),
        CONSTRAINT [ck_paper_workflow_steps_request_json] CHECK ([request_json] IS NULL OR ISJSON([request_json]) = 1),
        CONSTRAINT [ck_paper_workflow_steps_response_json] CHECK ([response_json] IS NULL OR ISJSON([response_json]) = 1),
        CONSTRAINT [ck_paper_workflow_steps_times] CHECK
        (
            ([status] = 'PENDING' AND [started_at_utc] IS NULL AND [completed_at_utc] IS NULL)
            OR ([status] = 'RUNNING' AND [started_at_utc] IS NOT NULL AND [completed_at_utc] IS NULL)
            OR ([status] IN ('SUCCEEDED','REJECTED','FAILED') AND [started_at_utc] IS NOT NULL AND [completed_at_utc] IS NOT NULL AND [completed_at_utc] >= [started_at_utc])
        )
    );

    CREATE INDEX [ix_paper_workflow_steps_workflow]
        ON [operations].[paper_workflow_steps] ([paper_workflow_id],[step_sequence])
        INCLUDE ([step_code],[status],[attempt_count],[retryable]);
END;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
THROW;
END CATCH;
GO
