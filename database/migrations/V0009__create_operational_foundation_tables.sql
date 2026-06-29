/*
Migration: V0009__create_operational_foundation_tables.sql
Purpose:
  Create durable inbox/outbox transport, scheduled-job state, incidents, operational
  controls and kill-switch projections, alerts, and append-only audit evidence.
Dependencies:
  V0001__create_schemas_and_migration_metadata.sql
  V0008__create_portfolio_and_pnl_tables.sql
Expected runtime impact:
  Additive DDL only. No messages, jobs, controls, incidents or audit events are backfilled.
Locking considerations:
  Schema modification locks are acquired while tables, constraints and indexes are created.
Backward-compatibility window:
  Fully additive.
Data migration requirements:
  None.
Verification script:
  database/verification/V0009__verify_operational_foundation_tables.sql
Recovery plan:
  Roll forward with a later migration. Destructive rollback is limited to disposable local databases.
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

    IF SCHEMA_ID(N'operations') IS NULL OR SCHEMA_ID(N'audit') IS NULL
        THROW 59001, 'V0001 is required: operations or audit schema does not exist.', 1;

    IF OBJECT_ID(N'[portfolio].[positions]', N'U') IS NULL
        THROW 59002, 'V0008 is required: portfolio.positions does not exist.', 1;

    IF OBJECT_ID(N'[operations].[outbox_messages]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[outbox_messages]
        (
            [outbox_message_id] bigint IDENTITY(1,1) NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [message_type] varchar(200) NOT NULL,
            [destination] varchar(200) NOT NULL,
            [partition_key] varchar(200) NULL,
            [aggregate_type] varchar(100) NULL,
            [aggregate_uid] uniqueidentifier NULL,
            [idempotency_key] varchar(200) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [not_before_utc] datetime2(7) NOT NULL,
            [expires_at_utc] datetime2(7) NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [payload_hash] char(64) NOT NULL,
            [headers_json] nvarchar(max) NULL,
            [status] varchar(30) NOT NULL
                CONSTRAINT [df_outbox_messages_status] DEFAULT ('PENDING'),
            [attempt_count] int NOT NULL
                CONSTRAINT [df_outbox_messages_attempt_count] DEFAULT (0),
            [max_attempts] int NOT NULL,
            [lease_owner] varchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [published_at_utc] datetime2(7) NULL,
            [dead_lettered_at_utc] datetime2(7) NULL,
            [last_error_code] varchar(100) NULL,
            [last_error_message] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_outbox_messages_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_outbox_messages_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_outbox_messages]
                PRIMARY KEY CLUSTERED ([outbox_message_id]),
            CONSTRAINT [uq_outbox_messages_uid]
                UNIQUE ([message_uid]),
            CONSTRAINT [ck_outbox_messages_contract_version]
                CHECK (LEN([contract_version]) BETWEEN 1 AND 20),
            CONSTRAINT [ck_outbox_messages_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_outbox_messages_time]
                CHECK
                (
                    [not_before_utc] >= [generated_at_utc]
                    AND ([expires_at_utc] IS NULL OR [expires_at_utc] > [generated_at_utc])
                ),
            CONSTRAINT [ck_outbox_messages_payload_json]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_outbox_messages_headers_json]
                CHECK ([headers_json] IS NULL OR ISJSON([headers_json]) = 1),
            CONSTRAINT [ck_outbox_messages_payload_hash]
                CHECK
                (
                    LEN(RTRIM([payload_hash])) = 64
                    AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_outbox_messages_status]
                CHECK
                (
                    [status] IN
                    ('PENDING', 'IN_FLIGHT', 'PUBLISHED', 'FAILED', 'DEAD_LETTER', 'CANCELLED', 'EXPIRED')
                ),
            CONSTRAINT [ck_outbox_messages_attempts]
                CHECK ([attempt_count] >= 0 AND [max_attempts] >= 1 AND [attempt_count] <= [max_attempts]),
            CONSTRAINT [ck_outbox_messages_lease]
                CHECK
                (
                    ([status] = 'IN_FLIGHT' AND [lease_owner] IS NOT NULL AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([status] <> 'IN_FLIGHT' AND [lease_owner] IS NULL AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_outbox_messages_terminal]
                CHECK
                (
                    ([status] = 'PUBLISHED' AND [published_at_utc] IS NOT NULL AND [dead_lettered_at_utc] IS NULL)
                    OR
                    ([status] = 'DEAD_LETTER' AND [dead_lettered_at_utc] IS NOT NULL AND [published_at_utc] IS NULL)
                    OR
                    ([status] NOT IN ('PUBLISHED', 'DEAD_LETTER')
                        AND [published_at_utc] IS NULL AND [dead_lettered_at_utc] IS NULL)
                )
        );

        CREATE UNIQUE INDEX [ux_outbox_messages_idempotency]
            ON [operations].[outbox_messages] ([environment], [destination], [idempotency_key])
            WHERE [idempotency_key] IS NOT NULL;

        CREATE INDEX [ix_outbox_messages_dispatch]
            ON [operations].[outbox_messages]
            ([status], [not_before_utc], [outbox_message_id])
            INCLUDE ([destination], [attempt_count], [max_attempts], [expires_at_utc]);

        CREATE INDEX [ix_outbox_messages_correlation]
            ON [operations].[outbox_messages] ([correlation_id], [created_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[outbox_delivery_attempts]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[outbox_delivery_attempts]
        (
            [outbox_delivery_attempt_id] bigint IDENTITY(1,1) NOT NULL,
            [outbox_message_id] bigint NOT NULL,
            [attempt_number] int NOT NULL,
            [dispatcher_instance] varchar(200) NOT NULL,
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [outcome] varchar(30) NOT NULL,
            [transport_status_code] varchar(100) NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(2000) NULL,
            [next_attempt_at_utc] datetime2(7) NULL,
            [response_metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_outbox_delivery_attempts_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_outbox_delivery_attempts]
                PRIMARY KEY CLUSTERED ([outbox_delivery_attempt_id]),
            CONSTRAINT [uq_outbox_delivery_attempts_number]
                UNIQUE ([outbox_message_id], [attempt_number]),
            CONSTRAINT [fk_outbox_delivery_attempts_message]
                FOREIGN KEY ([outbox_message_id])
                REFERENCES [operations].[outbox_messages] ([outbox_message_id]),
            CONSTRAINT [ck_outbox_delivery_attempts_number]
                CHECK ([attempt_number] >= 1),
            CONSTRAINT [ck_outbox_delivery_attempts_outcome]
                CHECK ([outcome] IN ('STARTED', 'SUCCEEDED', 'TRANSIENT_FAILURE', 'PERMANENT_FAILURE', 'UNKNOWN')),
            CONSTRAINT [ck_outbox_delivery_attempts_completion]
                CHECK
                (
                    ([outcome] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([outcome] <> 'STARTED'
                        AND [completed_at_utc] IS NOT NULL
                        AND [completed_at_utc] >= [started_at_utc])
                ),
            CONSTRAINT [ck_outbox_delivery_attempts_response_json]
                CHECK ([response_metadata_json] IS NULL OR ISJSON([response_metadata_json]) = 1)
        );

        CREATE INDEX [ix_outbox_delivery_attempts_latest]
            ON [operations].[outbox_delivery_attempts]
            ([outbox_message_id], [attempt_number] DESC)
            INCLUDE ([outcome], [completed_at_utc], [next_attempt_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[inbox_messages]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[inbox_messages]
        (
            [inbox_message_id] bigint IDENTITY(1,1) NOT NULL,
            [message_uid] uniqueidentifier NOT NULL,
            [consumer_name] varchar(200) NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [message_type] varchar(200) NOT NULL,
            [source_service] varchar(100) NOT NULL,
            [source_version] varchar(50) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [generated_at_utc] datetime2(7) NOT NULL,
            [received_at_utc] datetime2(7) NOT NULL,
            [expires_at_utc] datetime2(7) NULL,
            [payload_json] nvarchar(max) NOT NULL,
            [payload_hash] char(64) NOT NULL,
            [headers_json] nvarchar(max) NULL,
            [status] varchar(30) NOT NULL
                CONSTRAINT [df_inbox_messages_status] DEFAULT ('RECEIVED'),
            [attempt_count] int NOT NULL
                CONSTRAINT [df_inbox_messages_attempt_count] DEFAULT (0),
            [max_attempts] int NOT NULL,
            [lease_owner] varchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [processed_at_utc] datetime2(7) NULL,
            [dead_lettered_at_utc] datetime2(7) NULL,
            [result_reference] varchar(300) NULL,
            [last_error_code] varchar(100) NULL,
            [last_error_message] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_inbox_messages_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_inbox_messages_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_inbox_messages]
                PRIMARY KEY CLUSTERED ([inbox_message_id]),
            CONSTRAINT [uq_inbox_messages_consumer]
                UNIQUE ([consumer_name], [message_uid]),
            CONSTRAINT [ck_inbox_messages_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_inbox_messages_time]
                CHECK
                (
                    [received_at_utc] >= [generated_at_utc]
                    AND ([expires_at_utc] IS NULL OR [expires_at_utc] > [generated_at_utc])
                ),
            CONSTRAINT [ck_inbox_messages_payload_json]
                CHECK (ISJSON([payload_json]) = 1),
            CONSTRAINT [ck_inbox_messages_headers_json]
                CHECK ([headers_json] IS NULL OR ISJSON([headers_json]) = 1),
            CONSTRAINT [ck_inbox_messages_payload_hash]
                CHECK
                (
                    LEN(RTRIM([payload_hash])) = 64
                    AND [payload_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                ),
            CONSTRAINT [ck_inbox_messages_status]
                CHECK
                (
                    [status] IN
                    ('RECEIVED', 'PROCESSING', 'PROCESSED', 'REJECTED', 'FAILED', 'DEAD_LETTER', 'EXPIRED')
                ),
            CONSTRAINT [ck_inbox_messages_attempts]
                CHECK ([attempt_count] >= 0 AND [max_attempts] >= 1 AND [attempt_count] <= [max_attempts]),
            CONSTRAINT [ck_inbox_messages_lease]
                CHECK
                (
                    ([status] = 'PROCESSING' AND [lease_owner] IS NOT NULL AND [lease_expires_at_utc] IS NOT NULL)
                    OR
                    ([status] <> 'PROCESSING' AND [lease_owner] IS NULL AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_inbox_messages_terminal]
                CHECK
                (
                    ([status] = 'PROCESSED' AND [processed_at_utc] IS NOT NULL AND [dead_lettered_at_utc] IS NULL)
                    OR
                    ([status] = 'DEAD_LETTER' AND [dead_lettered_at_utc] IS NOT NULL AND [processed_at_utc] IS NULL)
                    OR
                    ([status] NOT IN ('PROCESSED', 'DEAD_LETTER')
                        AND [processed_at_utc] IS NULL AND [dead_lettered_at_utc] IS NULL)
                )
        );

        CREATE INDEX [ix_inbox_messages_processing]
            ON [operations].[inbox_messages]
            ([consumer_name], [status], [received_at_utc], [inbox_message_id])
            INCLUDE ([message_type], [attempt_count], [max_attempts], [expires_at_utc]);

        CREATE INDEX [ix_inbox_messages_correlation]
            ON [operations].[inbox_messages] ([correlation_id], [received_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[inbox_processing_attempts]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[inbox_processing_attempts]
        (
            [inbox_processing_attempt_id] bigint IDENTITY(1,1) NOT NULL,
            [inbox_message_id] bigint NOT NULL,
            [attempt_number] int NOT NULL,
            [processor_instance] varchar(200) NOT NULL,
            [started_at_utc] datetime2(7) NOT NULL,
            [completed_at_utc] datetime2(7) NULL,
            [outcome] varchar(30) NOT NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(2000) NULL,
            [next_attempt_at_utc] datetime2(7) NULL,
            [result_reference] varchar(300) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_inbox_processing_attempts_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_inbox_processing_attempts]
                PRIMARY KEY CLUSTERED ([inbox_processing_attempt_id]),
            CONSTRAINT [uq_inbox_processing_attempts_number]
                UNIQUE ([inbox_message_id], [attempt_number]),
            CONSTRAINT [fk_inbox_processing_attempts_message]
                FOREIGN KEY ([inbox_message_id])
                REFERENCES [operations].[inbox_messages] ([inbox_message_id]),
            CONSTRAINT [ck_inbox_processing_attempts_number]
                CHECK ([attempt_number] >= 1),
            CONSTRAINT [ck_inbox_processing_attempts_outcome]
                CHECK ([outcome] IN ('STARTED', 'SUCCEEDED', 'REJECTED', 'TRANSIENT_FAILURE', 'PERMANENT_FAILURE')),
            CONSTRAINT [ck_inbox_processing_attempts_completion]
                CHECK
                (
                    ([outcome] = 'STARTED' AND [completed_at_utc] IS NULL)
                    OR
                    ([outcome] <> 'STARTED'
                        AND [completed_at_utc] IS NOT NULL
                        AND [completed_at_utc] >= [started_at_utc])
                )
        );
    END;

    IF OBJECT_ID(N'[operations].[scheduled_jobs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[scheduled_jobs]
        (
            [scheduled_job_id] bigint IDENTITY(1,1) NOT NULL,
            [scheduled_job_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_scheduled_jobs_uid] DEFAULT NEWSEQUENTIALID(),
            [job_code] varchar(150) NOT NULL,
            [job_name] nvarchar(300) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [owner_service] varchar(100) NOT NULL,
            [handler_name] varchar(300) NOT NULL,
            [schedule_type] varchar(20) NOT NULL,
            [schedule_expression] varchar(300) NULL,
            [time_zone_id] varchar(100) NOT NULL,
            [concurrency_policy] varchar(30) NOT NULL,
            [misfire_policy] varchar(30) NOT NULL,
            [max_attempts] int NOT NULL,
            [timeout_seconds] int NOT NULL,
            [lease_seconds] int NOT NULL,
            [enabled] bit NOT NULL,
            [next_run_at_utc] datetime2(7) NULL,
            [last_run_at_utc] datetime2(7) NULL,
            [configuration_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_scheduled_jobs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_scheduled_jobs_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_scheduled_jobs]
                PRIMARY KEY CLUSTERED ([scheduled_job_id]),
            CONSTRAINT [uq_scheduled_jobs_uid]
                UNIQUE ([scheduled_job_uid]),
            CONSTRAINT [uq_scheduled_jobs_code]
                UNIQUE ([environment], [job_code]),
            CONSTRAINT [ck_scheduled_jobs_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_scheduled_jobs_schedule_type]
                CHECK ([schedule_type] IN ('CRON', 'INTERVAL', 'CALENDAR', 'ON_DEMAND')),
            CONSTRAINT [ck_scheduled_jobs_schedule_expression]
                CHECK
                (
                    ([schedule_type] = 'ON_DEMAND' AND [schedule_expression] IS NULL)
                    OR
                    ([schedule_type] <> 'ON_DEMAND' AND [schedule_expression] IS NOT NULL)
                ),
            CONSTRAINT [ck_scheduled_jobs_concurrency]
                CHECK ([concurrency_policy] IN ('DISALLOW_OVERLAP', 'ALLOW_OVERLAP', 'REPLACE_RUNNING')),
            CONSTRAINT [ck_scheduled_jobs_misfire]
                CHECK ([misfire_policy] IN ('SKIP', 'RUN_ONCE', 'CATCH_UP_BOUNDED')),
            CONSTRAINT [ck_scheduled_jobs_limits]
                CHECK ([max_attempts] >= 1 AND [timeout_seconds] >= 1 AND [lease_seconds] >= 1),
            CONSTRAINT [ck_scheduled_jobs_configuration_json]
                CHECK ([configuration_json] IS NULL OR ISJSON([configuration_json]) = 1)
        );

        CREATE INDEX [ix_scheduled_jobs_due]
            ON [operations].[scheduled_jobs]
            ([environment], [enabled], [next_run_at_utc], [scheduled_job_id])
            INCLUDE ([job_code], [owner_service], [concurrency_policy]);
    END;

    IF OBJECT_ID(N'[operations].[job_runs]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[job_runs]
        (
            [job_run_id] bigint IDENTITY(1,1) NOT NULL,
            [job_run_uid] uniqueidentifier NOT NULL,
            [scheduled_job_id] bigint NOT NULL,
            [idempotency_key] varchar(200) NOT NULL,
            [trigger_type] varchar(30) NOT NULL,
            [scheduled_for_utc] datetime2(7) NULL,
            [status] varchar(30) NOT NULL,
            [attempt_count] int NOT NULL,
            [max_attempts] int NOT NULL,
            [lease_owner] varchar(200) NULL,
            [lease_expires_at_utc] datetime2(7) NULL,
            [started_at_utc] datetime2(7) NULL,
            [completed_at_utc] datetime2(7) NULL,
            [heartbeat_at_utc] datetime2(7) NULL,
            [input_json] nvarchar(max) NULL,
            [output_json] nvarchar(max) NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(4000) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_job_runs_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_job_runs_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_job_runs]
                PRIMARY KEY CLUSTERED ([job_run_id]),
            CONSTRAINT [uq_job_runs_uid]
                UNIQUE ([job_run_uid]),
            CONSTRAINT [uq_job_runs_idempotency]
                UNIQUE ([scheduled_job_id], [idempotency_key]),
            CONSTRAINT [fk_job_runs_job]
                FOREIGN KEY ([scheduled_job_id])
                REFERENCES [operations].[scheduled_jobs] ([scheduled_job_id]),
            CONSTRAINT [ck_job_runs_trigger]
                CHECK ([trigger_type] IN ('SCHEDULED', 'MANUAL', 'RETRY', 'RECOVERY', 'DEPENDENCY')),
            CONSTRAINT [ck_job_runs_status]
                CHECK ([status] IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED', 'TIMED_OUT', 'DEAD_LETTER')),
            CONSTRAINT [ck_job_runs_attempts]
                CHECK ([attempt_count] >= 0 AND [max_attempts] >= 1 AND [attempt_count] <= [max_attempts]),
            CONSTRAINT [ck_job_runs_lease]
                CHECK
                (
                    ([status] = 'RUNNING' AND [lease_owner] IS NOT NULL AND [lease_expires_at_utc] IS NOT NULL
                        AND [started_at_utc] IS NOT NULL)
                    OR
                    ([status] <> 'RUNNING' AND [lease_owner] IS NULL AND [lease_expires_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_job_runs_completion]
                CHECK
                (
                    ([status] IN ('QUEUED', 'RUNNING') AND [completed_at_utc] IS NULL)
                    OR
                    ([status] NOT IN ('QUEUED', 'RUNNING')
                        AND [completed_at_utc] IS NOT NULL
                        AND ([started_at_utc] IS NULL OR [completed_at_utc] >= [started_at_utc]))
                ),
            CONSTRAINT [ck_job_runs_input_json]
                CHECK ([input_json] IS NULL OR ISJSON([input_json]) = 1),
            CONSTRAINT [ck_job_runs_output_json]
                CHECK ([output_json] IS NULL OR ISJSON([output_json]) = 1)
        );

        CREATE INDEX [ix_job_runs_dispatch]
            ON [operations].[job_runs]
            ([status], [created_at_utc], [job_run_id])
            INCLUDE ([scheduled_job_id], [attempt_count], [max_attempts], [lease_expires_at_utc]);

        CREATE INDEX [ix_job_runs_job_time]
            ON [operations].[job_runs]
            ([scheduled_job_id], [created_at_utc] DESC)
            INCLUDE ([status], [started_at_utc], [completed_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[job_run_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[job_run_events]
        (
            [job_run_event_id] bigint IDENTITY(1,1) NOT NULL,
            [job_run_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [event_type] varchar(40) NOT NULL,
            [status_before] varchar(30) NULL,
            [status_after] varchar(30) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [actor] nvarchar(256) NOT NULL,
            [reason_code] varchar(100) NULL,
            [reason_message] nvarchar(2000) NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_job_run_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_job_run_events]
                PRIMARY KEY CLUSTERED ([job_run_event_id]),
            CONSTRAINT [uq_job_run_events_sequence]
                UNIQUE ([job_run_id], [event_sequence]),
            CONSTRAINT [fk_job_run_events_run]
                FOREIGN KEY ([job_run_id])
                REFERENCES [operations].[job_runs] ([job_run_id]),
            CONSTRAINT [ck_job_run_events_sequence]
                CHECK ([event_sequence] >= 1),
            CONSTRAINT [ck_job_run_events_type]
                CHECK
                (
                    [event_type] IN
                    ('QUEUED', 'LEASED', 'STARTED', 'HEARTBEAT', 'SUCCEEDED', 'FAILED',
                     'RETRY_SCHEDULED', 'CANCELLED', 'TIMED_OUT', 'DEAD_LETTERED', 'RECOVERED')
                ),
            CONSTRAINT [ck_job_run_events_status_after]
                CHECK ([status_after] IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED', 'TIMED_OUT', 'DEAD_LETTER')),
            CONSTRAINT [ck_job_run_events_status_before]
                CHECK
                (
                    [status_before] IS NULL
                    OR [status_before] IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED', 'TIMED_OUT', 'DEAD_LETTER')
                ),
            CONSTRAINT [ck_job_run_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );
    END;

    IF OBJECT_ID(N'[operations].[incidents]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[incidents]
        (
            [incident_id] bigint IDENTITY(1,1) NOT NULL,
            [incident_uid] uniqueidentifier NOT NULL,
            [environment] varchar(20) NOT NULL,
            [incident_key] varchar(200) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [category] varchar(50) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [title] nvarchar(300) NOT NULL,
            [description] nvarchar(4000) NOT NULL,
            [detection_source] varchar(50) NOT NULL,
            [status] varchar(30) NOT NULL,
            [operating_mode_before] varchar(20) NULL,
            [operating_mode_after] varchar(20) NULL,
            [reconciliation_status] varchar(30) NOT NULL,
            [owner] nvarchar(256) NULL,
            [first_occurred_at_utc] datetime2(7) NOT NULL,
            [latest_occurred_at_utc] datetime2(7) NOT NULL,
            [acknowledged_at_utc] datetime2(7) NULL,
            [acknowledged_by] nvarchar(256) NULL,
            [contained_at_utc] datetime2(7) NULL,
            [resolved_at_utc] datetime2(7) NULL,
            [closed_at_utc] datetime2(7) NULL,
            [resolution_summary] nvarchar(4000) NULL,
            [prevention_actions_json] nvarchar(max) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_incidents_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_incidents_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_incidents]
                PRIMARY KEY CLUSTERED ([incident_id]),
            CONSTRAINT [uq_incidents_uid]
                UNIQUE ([incident_uid]),
            CONSTRAINT [uq_incidents_key]
                UNIQUE ([environment], [incident_key]),
            CONSTRAINT [ck_incidents_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_incidents_severity]
                CHECK ([severity] IN ('INFO', 'WARNING', 'MAJOR', 'CRITICAL')),
            CONSTRAINT [ck_incidents_scope]
                CHECK
                (
                    [scope_type] IN
                    ('PLATFORM', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY', 'MODEL',
                     'ENGINE', 'INSTRUMENT', 'INSTRUMENT_CLASS', 'SEGMENT', 'ACTION_TYPE',
                     'ORDER', 'POSITION', 'SERVICE', 'JOB')
                ),
            CONSTRAINT [ck_incidents_detection_source]
                CHECK
                (
                    [detection_source] IN
                    ('AUTOMATIC', 'OPERATOR', 'RISK_POLICY', 'DATA_QUALITY', 'BROKER',
                     'SECURITY', 'RECONCILIATION', 'SERVICE_HEALTH', 'SCHEDULER')
                ),
            CONSTRAINT [ck_incidents_status]
                CHECK ([status] IN ('OPEN', 'ACKNOWLEDGED', 'CONTAINED', 'RESOLVED', 'CLOSED')),
            CONSTRAINT [ck_incidents_modes]
                CHECK
                (
                    ([operating_mode_before] IS NULL OR [operating_mode_before] IN
                        ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY'))
                    AND
                    ([operating_mode_after] IS NULL OR [operating_mode_after] IN
                        ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY'))
                ),
            CONSTRAINT [ck_incidents_reconciliation]
                CHECK ([reconciliation_status] IN ('NOT_REQUIRED', 'PENDING', 'IN_PROGRESS', 'SUCCEEDED', 'FAILED')),
            CONSTRAINT [ck_incidents_time]
                CHECK ([latest_occurred_at_utc] >= [first_occurred_at_utc]),
            CONSTRAINT [ck_incidents_lifecycle]
                CHECK
                (
                    ([status] = 'OPEN'
                        AND [acknowledged_at_utc] IS NULL AND [contained_at_utc] IS NULL
                        AND [resolved_at_utc] IS NULL AND [closed_at_utc] IS NULL)
                    OR
                    ([status] = 'ACKNOWLEDGED'
                        AND [acknowledged_at_utc] IS NOT NULL AND [acknowledged_by] IS NOT NULL
                        AND [resolved_at_utc] IS NULL AND [closed_at_utc] IS NULL)
                    OR
                    ([status] = 'CONTAINED'
                        AND [acknowledged_at_utc] IS NOT NULL AND [contained_at_utc] IS NOT NULL
                        AND [resolved_at_utc] IS NULL AND [closed_at_utc] IS NULL)
                    OR
                    ([status] = 'RESOLVED'
                        AND [resolved_at_utc] IS NOT NULL AND [closed_at_utc] IS NULL
                        AND [resolution_summary] IS NOT NULL)
                    OR
                    ([status] = 'CLOSED'
                        AND [resolved_at_utc] IS NOT NULL AND [closed_at_utc] IS NOT NULL
                        AND [resolution_summary] IS NOT NULL)
                ),
            CONSTRAINT [ck_incidents_prevention_json]
                CHECK ([prevention_actions_json] IS NULL OR ISJSON([prevention_actions_json]) = 1)
        );

        CREATE INDEX [ix_incidents_open]
            ON [operations].[incidents]
            ([environment], [status], [severity], [latest_occurred_at_utc] DESC)
            INCLUDE ([incident_key], [category], [scope_type], [scope_id], [owner]);
    END;

    IF OBJECT_ID(N'[operations].[incident_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[incident_events]
        (
            [incident_event_id] bigint IDENTITY(1,1) NOT NULL,
            [incident_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_incident_events_uid] DEFAULT NEWSEQUENTIALID(),
            [incident_id] bigint NOT NULL,
            [event_sequence] int NOT NULL,
            [event_type] varchar(40) NOT NULL,
            [status_before] varchar(30) NULL,
            [status_after] varchar(30) NOT NULL,
            [severity_before] varchar(20) NULL,
            [severity_after] varchar(20) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [actor_type] varchar(20) NOT NULL,
            [actor_id] nvarchar(256) NOT NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_message] nvarchar(4000) NULL,
            [action_json] nvarchar(max) NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_incident_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_incident_events]
                PRIMARY KEY CLUSTERED ([incident_event_id]),
            CONSTRAINT [uq_incident_events_uid]
                UNIQUE ([incident_event_uid]),
            CONSTRAINT [uq_incident_events_sequence]
                UNIQUE ([incident_id], [event_sequence]),
            CONSTRAINT [fk_incident_events_incident]
                FOREIGN KEY ([incident_id])
                REFERENCES [operations].[incidents] ([incident_id]),
            CONSTRAINT [ck_incident_events_sequence]
                CHECK ([event_sequence] >= 1),
            CONSTRAINT [ck_incident_events_type]
                CHECK
                (
                    [event_type] IN
                    ('DETECTED', 'OCCURRENCE_RECORDED', 'ACKNOWLEDGED', 'ASSIGNED', 'ESCALATED',
                     'CONTAINMENT_APPLIED', 'RECONCILIATION_STARTED', 'RECONCILIATION_COMPLETED',
                     'RESOLUTION_PROPOSED', 'RESOLVED', 'CLOSED', 'REOPENED', 'COMMENTED')
                ),
            CONSTRAINT [ck_incident_events_status]
                CHECK
                (
                    [status_after] IN ('OPEN', 'ACKNOWLEDGED', 'CONTAINED', 'RESOLVED', 'CLOSED')
                    AND ([status_before] IS NULL OR [status_before] IN
                        ('OPEN', 'ACKNOWLEDGED', 'CONTAINED', 'RESOLVED', 'CLOSED'))
                ),
            CONSTRAINT [ck_incident_events_severity]
                CHECK
                (
                    [severity_after] IN ('INFO', 'WARNING', 'MAJOR', 'CRITICAL')
                    AND ([severity_before] IS NULL OR [severity_before] IN
                        ('INFO', 'WARNING', 'MAJOR', 'CRITICAL'))
                ),
            CONSTRAINT [ck_incident_events_actor]
                CHECK ([actor_type] IN ('USER', 'SERVICE', 'POLICY', 'SYSTEM')),
            CONSTRAINT [ck_incident_events_action_json]
                CHECK ([action_json] IS NULL OR ISJSON([action_json]) = 1)
        );

        CREATE INDEX [ix_incident_events_latest]
            ON [operations].[incident_events]
            ([incident_id], [event_sequence] DESC)
            INCLUDE ([event_type], [status_after], [severity_after], [occurred_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[incident_entity_links]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[incident_entity_links]
        (
            [incident_entity_link_id] bigint IDENTITY(1,1) NOT NULL,
            [incident_id] bigint NOT NULL,
            [entity_type] varchar(50) NOT NULL,
            [entity_reference] varchar(200) NOT NULL,
            [relationship_type] varchar(40) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_incident_entity_links_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_incident_entity_links]
                PRIMARY KEY CLUSTERED ([incident_entity_link_id]),
            CONSTRAINT [uq_incident_entity_links_entity]
                UNIQUE ([incident_id], [entity_type], [entity_reference], [relationship_type]),
            CONSTRAINT [fk_incident_entity_links_incident]
                FOREIGN KEY ([incident_id])
                REFERENCES [operations].[incidents] ([incident_id]),
            CONSTRAINT [ck_incident_entity_links_relationship]
                CHECK ([relationship_type] IN ('AFFECTS', 'CAUSED_BY', 'DETECTED_BY', 'RESOLVED_BY', 'RELATED_TO'))
        );
    END;

    IF OBJECT_ID(N'[operations].[operational_controls]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[operational_controls]
        (
            [operational_control_id] bigint IDENTITY(1,1) NOT NULL,
            [control_uid] uniqueidentifier NOT NULL,
            [contract_version] varchar(20) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [control_type] varchar(30) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [operating_mode] varchar(20) NOT NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_message] nvarchar(4000) NULL,
            [trigger_source] varchar(30) NULL,
            [activated_at_utc] datetime2(7) NOT NULL,
            [activated_by] nvarchar(200) NOT NULL,
            [expires_at_utc] datetime2(7) NULL,
            [reset_requires_approval] bit NOT NULL
                CONSTRAINT [df_operational_controls_reset_approval] DEFAULT (1),
            [policy_version] varchar(100) NOT NULL,
            [incident_id] bigint NULL,
            [correlation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [raw_contract_json] nvarchar(max) NOT NULL,
            [contract_hash] char(64) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_operational_controls_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_operational_controls]
                PRIMARY KEY CLUSTERED ([operational_control_id]),
            CONSTRAINT [uq_operational_controls_uid]
                UNIQUE ([control_uid]),
            CONSTRAINT [fk_operational_controls_incident]
                FOREIGN KEY ([incident_id])
                REFERENCES [operations].[incidents] ([incident_id]),
            CONSTRAINT [ck_operational_controls_contract_version]
                CHECK ([contract_version] = '1.0.0'),
            CONSTRAINT [ck_operational_controls_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_operational_controls_type]
                CHECK ([control_type] IN ('KILL_SWITCH', 'PAUSE', 'CLOSE_ONLY', 'RESTRICTION', 'RECOVERY')),
            CONSTRAINT [ck_operational_controls_scope]
                CHECK
                (
                    [scope_type] IN
                    ('PLATFORM', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY', 'MODEL', 'ENGINE',
                     'INSTRUMENT', 'INSTRUMENT_CLASS', 'SEGMENT', 'ACTION_TYPE')
                ),
            CONSTRAINT [ck_operational_controls_mode]
                CHECK ([operating_mode] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY')),
            CONSTRAINT [ck_operational_controls_trigger]
                CHECK
                (
                    [trigger_source] IS NULL OR [trigger_source] IN
                    ('AUTOMATIC', 'OPERATOR', 'RISK_POLICY', 'DATA_QUALITY', 'BROKER',
                     'SECURITY', 'RECONCILIATION')
                ),
            CONSTRAINT [ck_operational_controls_expiry]
                CHECK ([expires_at_utc] IS NULL OR [expires_at_utc] > [activated_at_utc]),
            CONSTRAINT [ck_operational_controls_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_operational_controls_raw_json]
                CHECK (ISJSON([raw_contract_json]) = 1),
            CONSTRAINT [ck_operational_controls_hash]
                CHECK
                (
                    LEN(RTRIM([contract_hash])) = 64
                    AND [contract_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_operational_controls_scope]
            ON [operations].[operational_controls]
            ([environment], [scope_type], [scope_id], [activated_at_utc] DESC)
            INCLUDE ([control_type], [operating_mode], [expires_at_utc], [policy_version]);
    END;

    IF OBJECT_ID(N'[operations].[operational_control_approvals]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[operational_control_approvals]
        (
            [operational_control_approval_id] bigint IDENTITY(1,1) NOT NULL,
            [approval_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_operational_control_approvals_uid] DEFAULT NEWSEQUENTIALID(),
            [operational_control_id] bigint NOT NULL,
            [approval_type] varchar(30) NOT NULL,
            [status] varchar(20) NOT NULL,
            [requested_at_utc] datetime2(7) NOT NULL,
            [requested_by] nvarchar(256) NOT NULL,
            [trigger_actor] nvarchar(256) NULL,
            [independent_approver_required] bit NOT NULL,
            [decided_at_utc] datetime2(7) NULL,
            [decided_by] nvarchar(256) NULL,
            [decision_reason] nvarchar(4000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_operational_control_approvals_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_operational_control_approvals]
                PRIMARY KEY CLUSTERED ([operational_control_approval_id]),
            CONSTRAINT [uq_operational_control_approvals_uid]
                UNIQUE ([approval_uid]),
            CONSTRAINT [fk_operational_control_approvals_control]
                FOREIGN KEY ([operational_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [ck_operational_control_approvals_type]
                CHECK ([approval_type] IN ('ACTIVATE', 'RESET', 'EXTEND', 'SUPERSEDE')),
            CONSTRAINT [ck_operational_control_approvals_status]
                CHECK ([status] IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED')),
            CONSTRAINT [ck_operational_control_approvals_decision]
                CHECK
                (
                    ([status] = 'PENDING' AND [decided_at_utc] IS NULL AND [decided_by] IS NULL)
                    OR
                    ([status] <> 'PENDING'
                        AND [decided_at_utc] IS NOT NULL
                        AND [decided_by] IS NOT NULL
                        AND [decided_at_utc] >= [requested_at_utc])
                ),
            CONSTRAINT [ck_operational_control_approvals_independent]
                CHECK
                (
                    [status] <> 'APPROVED'
                    OR [independent_approver_required] = 0
                    OR [trigger_actor] IS NULL
                    OR [decided_by] <> [trigger_actor]
                )
        );

        CREATE INDEX [ix_operational_control_approvals_pending]
            ON [operations].[operational_control_approvals]
            ([status], [requested_at_utc])
            INCLUDE ([operational_control_id], [approval_type], [independent_approver_required]);
    END;

    IF OBJECT_ID(N'[operations].[operational_control_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[operational_control_states]
        (
            [operational_control_state_id] bigint IDENTITY(1,1) NOT NULL,
            [operational_control_id] bigint NOT NULL,
            [status] varchar(20) NOT NULL,
            [current_operating_mode] varchar(20) NOT NULL,
            [last_event_sequence] int NOT NULL,
            [reset_at_utc] datetime2(7) NULL,
            [reset_by] nvarchar(200) NULL,
            [reset_reason] nvarchar(4000) NULL,
            [superseded_by_control_id] bigint NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_operational_control_states_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_operational_control_states]
                PRIMARY KEY CLUSTERED ([operational_control_state_id]),
            CONSTRAINT [uq_operational_control_states_control]
                UNIQUE ([operational_control_id]),
            CONSTRAINT [fk_operational_control_states_control]
                FOREIGN KEY ([operational_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [fk_operational_control_states_superseding_control]
                FOREIGN KEY ([superseded_by_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [ck_operational_control_states_status]
                CHECK ([status] IN ('ACTIVE', 'EXPIRED', 'RESET', 'SUPERSEDED')),
            CONSTRAINT [ck_operational_control_states_mode]
                CHECK ([current_operating_mode] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY')),
            CONSTRAINT [ck_operational_control_states_sequence]
                CHECK ([last_event_sequence] >= 0),
            CONSTRAINT [ck_operational_control_states_reset]
                CHECK
                (
                    ([status] = 'RESET'
                        AND [reset_at_utc] IS NOT NULL AND [reset_by] IS NOT NULL AND [reset_reason] IS NOT NULL)
                    OR
                    ([status] <> 'RESET'
                        AND [reset_at_utc] IS NULL AND [reset_by] IS NULL AND [reset_reason] IS NULL)
                ),
            CONSTRAINT [ck_operational_control_states_superseded]
                CHECK
                (
                    ([status] = 'SUPERSEDED' AND [superseded_by_control_id] IS NOT NULL)
                    OR
                    ([status] <> 'SUPERSEDED' AND [superseded_by_control_id] IS NULL)
                )
        );

        CREATE INDEX [ix_operational_control_states_active]
            ON [operations].[operational_control_states]
            ([status], [updated_at_utc] DESC)
            INCLUDE ([operational_control_id], [current_operating_mode], [last_event_sequence]);
    END;

    IF OBJECT_ID(N'[operations].[operational_control_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[operational_control_events]
        (
            [operational_control_event_id] bigint IDENTITY(1,1) NOT NULL,
            [operational_control_event_uid] uniqueidentifier NOT NULL
                CONSTRAINT [df_operational_control_events_uid] DEFAULT NEWSEQUENTIALID(),
            [operational_control_id] bigint NOT NULL,
            [operational_control_approval_id] bigint NULL,
            [event_sequence] int NOT NULL,
            [event_type] varchar(30) NOT NULL,
            [status_before] varchar(20) NULL,
            [status_after] varchar(20) NOT NULL,
            [mode_before] varchar(20) NULL,
            [mode_after] varchar(20) NOT NULL,
            [occurred_at_utc] datetime2(7) NOT NULL,
            [actor_type] varchar(20) NOT NULL,
            [actor_id] nvarchar(256) NOT NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_message] nvarchar(4000) NULL,
            [correlation_id] uniqueidentifier NULL,
            [causation_id] uniqueidentifier NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_operational_control_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_operational_control_events]
                PRIMARY KEY CLUSTERED ([operational_control_event_id]),
            CONSTRAINT [uq_operational_control_events_uid]
                UNIQUE ([operational_control_event_uid]),
            CONSTRAINT [uq_operational_control_events_sequence]
                UNIQUE ([operational_control_id], [event_sequence]),
            CONSTRAINT [fk_operational_control_events_control]
                FOREIGN KEY ([operational_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [fk_operational_control_events_approval]
                FOREIGN KEY ([operational_control_approval_id])
                REFERENCES [operations].[operational_control_approvals] ([operational_control_approval_id]),
            CONSTRAINT [ck_operational_control_events_sequence]
                CHECK ([event_sequence] >= 1),
            CONSTRAINT [ck_operational_control_events_type]
                CHECK ([event_type] IN ('ACTIVATED', 'MODE_CHANGED', 'EXTENDED', 'EXPIRED', 'RESET', 'SUPERSEDED')),
            CONSTRAINT [ck_operational_control_events_status]
                CHECK
                (
                    [status_after] IN ('ACTIVE', 'EXPIRED', 'RESET', 'SUPERSEDED')
                    AND ([status_before] IS NULL OR [status_before] IN ('ACTIVE', 'EXPIRED', 'RESET', 'SUPERSEDED'))
                ),
            CONSTRAINT [ck_operational_control_events_modes]
                CHECK
                (
                    [mode_after] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY')
                    AND ([mode_before] IS NULL OR [mode_before] IN
                        ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY'))
                ),
            CONSTRAINT [ck_operational_control_events_actor]
                CHECK ([actor_type] IN ('USER', 'SERVICE', 'POLICY', 'SYSTEM')),
            CONSTRAINT [ck_operational_control_events_approval]
                CHECK
                (
                    [event_type] NOT IN ('RESET', 'EXTENDED', 'SUPERSEDED')
                    OR [operational_control_approval_id] IS NOT NULL
                ),
            CONSTRAINT [ck_operational_control_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1)
        );

        CREATE INDEX [ix_operational_control_events_latest]
            ON [operations].[operational_control_events]
            ([operational_control_id], [event_sequence] DESC)
            INCLUDE ([event_type], [status_after], [mode_after], [occurred_at_utc]);
    END;

    IF OBJECT_ID(N'[operations].[scope_operating_states]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[scope_operating_states]
        (
            [scope_operating_state_id] bigint IDENTITY(1,1) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [effective_operating_mode] varchar(20) NOT NULL,
            [source_operational_control_id] bigint NULL,
            [allows_new_exposure] bit NOT NULL,
            [allows_risk_reducing_exits] bit NOT NULL,
            [requires_operator_review] bit NOT NULL,
            [evaluated_at_utc] datetime2(7) NOT NULL,
            [evaluation_version] varchar(100) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_scope_operating_states_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_scope_operating_states]
                PRIMARY KEY CLUSTERED ([scope_operating_state_id]),
            CONSTRAINT [uq_scope_operating_states_scope]
                UNIQUE ([environment], [scope_type], [scope_id]),
            CONSTRAINT [fk_scope_operating_states_control]
                FOREIGN KEY ([source_operational_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [ck_scope_operating_states_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_scope_operating_states_scope]
                CHECK
                (
                    [scope_type] IN
                    ('PLATFORM', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY', 'MODEL', 'ENGINE',
                     'INSTRUMENT', 'INSTRUMENT_CLASS', 'SEGMENT', 'ACTION_TYPE')
                ),
            CONSTRAINT [ck_scope_operating_states_mode]
                CHECK ([effective_operating_mode] IN ('NORMAL', 'RESTRICTED', 'CLOSE_ONLY', 'PAUSED', 'HALTED', 'RECOVERY')),
            CONSTRAINT [ck_scope_operating_states_exit_safety]
                CHECK ([allows_new_exposure] = 0 OR [allows_risk_reducing_exits] = 1),
            CONSTRAINT [ck_scope_operating_states_control]
                CHECK
                (
                    ([effective_operating_mode] = 'NORMAL' AND [source_operational_control_id] IS NULL)
                    OR
                    ([effective_operating_mode] <> 'NORMAL' AND [source_operational_control_id] IS NOT NULL)
                )
        );

        CREATE INDEX [ix_scope_operating_states_mode]
            ON [operations].[scope_operating_states]
            ([environment], [effective_operating_mode], [scope_type])
            INCLUDE ([scope_id], [allows_new_exposure], [allows_risk_reducing_exits], [requires_operator_review]);
    END;

    IF OBJECT_ID(N'[operations].[alerts]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[alerts]
        (
            [alert_id] bigint IDENTITY(1,1) NOT NULL,
            [alert_uid] uniqueidentifier NOT NULL,
            [environment] varchar(20) NOT NULL,
            [alert_key] varchar(200) NOT NULL,
            [severity] varchar(20) NOT NULL,
            [category] varchar(50) NOT NULL,
            [scope_type] varchar(30) NOT NULL,
            [scope_id] varchar(200) NOT NULL,
            [title] nvarchar(300) NOT NULL,
            [message] nvarchar(4000) NOT NULL,
            [status] varchar(20) NOT NULL,
            [occurrence_count] int NOT NULL,
            [first_occurred_at_utc] datetime2(7) NOT NULL,
            [latest_occurred_at_utc] datetime2(7) NOT NULL,
            [acknowledged_at_utc] datetime2(7) NULL,
            [acknowledged_by] nvarchar(256) NULL,
            [resolved_at_utc] datetime2(7) NULL,
            [resolved_by] nvarchar(256) NULL,
            [suppressed_until_utc] datetime2(7) NULL,
            [incident_id] bigint NULL,
            [correlation_id] uniqueidentifier NULL,
            [details_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_alerts_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            [updated_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_alerts_updated_at_utc] DEFAULT SYSUTCDATETIME(),
            [updated_by] nvarchar(256) NOT NULL,
            [row_version] rowversion NOT NULL,
            CONSTRAINT [pk_alerts]
                PRIMARY KEY CLUSTERED ([alert_id]),
            CONSTRAINT [uq_alerts_uid]
                UNIQUE ([alert_uid]),
            CONSTRAINT [fk_alerts_incident]
                FOREIGN KEY ([incident_id])
                REFERENCES [operations].[incidents] ([incident_id]),
            CONSTRAINT [ck_alerts_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_alerts_severity]
                CHECK ([severity] IN ('INFO', 'WARNING', 'MAJOR', 'CRITICAL')),
            CONSTRAINT [ck_alerts_scope]
                CHECK
                (
                    [scope_type] IN
                    ('PLATFORM', 'ENVIRONMENT', 'BROKER_ACCOUNT', 'STRATEGY', 'MODEL',
                     'ENGINE', 'INSTRUMENT', 'SEGMENT', 'ORDER', 'POSITION', 'SERVICE', 'JOB')
                ),
            CONSTRAINT [ck_alerts_status]
                CHECK ([status] IN ('OPEN', 'ACKNOWLEDGED', 'RESOLVED', 'SUPPRESSED')),
            CONSTRAINT [ck_alerts_count]
                CHECK ([occurrence_count] >= 1),
            CONSTRAINT [ck_alerts_time]
                CHECK ([latest_occurred_at_utc] >= [first_occurred_at_utc]),
            CONSTRAINT [ck_alerts_lifecycle]
                CHECK
                (
                    ([status] = 'OPEN'
                        AND [acknowledged_at_utc] IS NULL AND [resolved_at_utc] IS NULL
                        AND [suppressed_until_utc] IS NULL)
                    OR
                    ([status] = 'ACKNOWLEDGED'
                        AND [acknowledged_at_utc] IS NOT NULL AND [acknowledged_by] IS NOT NULL
                        AND [resolved_at_utc] IS NULL AND [suppressed_until_utc] IS NULL)
                    OR
                    ([status] = 'RESOLVED'
                        AND [resolved_at_utc] IS NOT NULL AND [resolved_by] IS NOT NULL
                        AND [suppressed_until_utc] IS NULL)
                    OR
                    ([status] = 'SUPPRESSED'
                        AND [suppressed_until_utc] IS NOT NULL AND [resolved_at_utc] IS NULL)
                ),
            CONSTRAINT [ck_alerts_details_json]
                CHECK ([details_json] IS NULL OR ISJSON([details_json]) = 1)
        );

        CREATE INDEX [ix_alerts_open]
            ON [operations].[alerts]
            ([environment], [status], [severity], [latest_occurred_at_utc] DESC)
            INCLUDE ([alert_key], [category], [scope_type], [scope_id], [incident_id]);

        CREATE INDEX [ix_alerts_dedupe]
            ON [operations].[alerts]
            ([environment], [alert_key], [latest_occurred_at_utc] DESC);
    END;

    IF OBJECT_ID(N'[operations].[alert_deliveries]', N'U') IS NULL
    BEGIN
        CREATE TABLE [operations].[alert_deliveries]
        (
            [alert_delivery_id] bigint IDENTITY(1,1) NOT NULL,
            [alert_id] bigint NOT NULL,
            [channel] varchar(30) NOT NULL,
            [destination_reference] varchar(200) NOT NULL,
            [attempt_number] int NOT NULL,
            [status] varchar(20) NOT NULL,
            [requested_at_utc] datetime2(7) NOT NULL,
            [delivered_at_utc] datetime2(7) NULL,
            [provider_message_id] varchar(200) NULL,
            [error_code] varchar(100) NULL,
            [error_message] nvarchar(2000) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_alert_deliveries_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_alert_deliveries]
                PRIMARY KEY CLUSTERED ([alert_delivery_id]),
            CONSTRAINT [uq_alert_deliveries_attempt]
                UNIQUE ([alert_id], [channel], [destination_reference], [attempt_number]),
            CONSTRAINT [fk_alert_deliveries_alert]
                FOREIGN KEY ([alert_id])
                REFERENCES [operations].[alerts] ([alert_id]),
            CONSTRAINT [ck_alert_deliveries_channel]
                CHECK ([channel] IN ('EMAIL', 'SMS', 'PUSH', 'SLACK', 'WEBHOOK', 'DASHBOARD')),
            CONSTRAINT [ck_alert_deliveries_attempt]
                CHECK ([attempt_number] >= 1),
            CONSTRAINT [ck_alert_deliveries_status]
                CHECK ([status] IN ('PENDING', 'DELIVERED', 'FAILED', 'CANCELLED')),
            CONSTRAINT [ck_alert_deliveries_completion]
                CHECK
                (
                    ([status] = 'DELIVERED' AND [delivered_at_utc] IS NOT NULL)
                    OR
                    ([status] <> 'DELIVERED' AND [delivered_at_utc] IS NULL)
                )
        );

        CREATE INDEX [ix_alert_deliveries_pending]
            ON [operations].[alert_deliveries]
            ([status], [requested_at_utc])
            INCLUDE ([alert_id], [channel], [attempt_number]);
    END;

    IF OBJECT_ID(N'[audit].[audit_events]', N'U') IS NULL
    BEGIN
        CREATE TABLE [audit].[audit_events]
        (
            [audit_event_id] bigint IDENTITY(1,1) NOT NULL,
            [audit_event_uid] uniqueidentifier NOT NULL,
            [audit_partition_key] varchar(200) NOT NULL,
            [partition_sequence] bigint NOT NULL,
            [event_type] varchar(100) NOT NULL,
            [action] varchar(100) NOT NULL,
            [environment] varchar(20) NOT NULL,
            [entity_type] varchar(100) NOT NULL,
            [entity_reference] varchar(200) NOT NULL,
            [actor_type] varchar(20) NOT NULL,
            [actor_id] nvarchar(256) NOT NULL,
            [actor_service_version] varchar(50) NULL,
            [reason_code] varchar(100) NOT NULL,
            [reason_message] nvarchar(4000) NULL,
            [approval_reference] varchar(200) NULL,
            [before_json] nvarchar(max) NULL,
            [after_json] nvarchar(max) NULL,
            [event_at_utc] datetime2(7) NOT NULL,
            [persisted_at_utc] datetime2(7) NOT NULL,
            [correlation_id] uniqueidentifier NOT NULL,
            [causation_id] uniqueidentifier NULL,
            [incident_id] bigint NULL,
            [operational_control_id] bigint NULL,
            [previous_event_hash] char(64) NULL,
            [event_hash] char(64) NOT NULL,
            [metadata_json] nvarchar(max) NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_audit_events_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_audit_events]
                PRIMARY KEY CLUSTERED ([audit_event_id]),
            CONSTRAINT [uq_audit_events_uid]
                UNIQUE ([audit_event_uid]),
            CONSTRAINT [uq_audit_events_partition_sequence]
                UNIQUE ([audit_partition_key], [partition_sequence]),
            CONSTRAINT [fk_audit_events_incident]
                FOREIGN KEY ([incident_id])
                REFERENCES [operations].[incidents] ([incident_id]),
            CONSTRAINT [fk_audit_events_control]
                FOREIGN KEY ([operational_control_id])
                REFERENCES [operations].[operational_controls] ([operational_control_id]),
            CONSTRAINT [ck_audit_events_environment]
                CHECK ([environment] IN ('PAPER', 'SHADOW', 'LIVE')),
            CONSTRAINT [ck_audit_events_actor]
                CHECK ([actor_type] IN ('USER', 'SERVICE', 'POLICY', 'SYSTEM')),
            CONSTRAINT [ck_audit_events_sequence]
                CHECK ([partition_sequence] >= 1),
            CONSTRAINT [ck_audit_events_values]
                CHECK ([before_json] IS NOT NULL OR [after_json] IS NOT NULL),
            CONSTRAINT [ck_audit_events_before_json]
                CHECK ([before_json] IS NULL OR ISJSON([before_json]) = 1),
            CONSTRAINT [ck_audit_events_after_json]
                CHECK ([after_json] IS NULL OR ISJSON([after_json]) = 1),
            CONSTRAINT [ck_audit_events_metadata_json]
                CHECK ([metadata_json] IS NULL OR ISJSON([metadata_json]) = 1),
            CONSTRAINT [ck_audit_events_time]
                CHECK ([persisted_at_utc] >= [event_at_utc]),
            CONSTRAINT [ck_audit_events_previous_hash]
                CHECK
                (
                    [previous_event_hash] IS NULL
                    OR
                    (
                        LEN(RTRIM([previous_event_hash])) = 64
                        AND [previous_event_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                    )
                ),
            CONSTRAINT [ck_audit_events_hash]
                CHECK
                (
                    LEN(RTRIM([event_hash])) = 64
                    AND [event_hash] NOT LIKE '%[^0-9A-Fa-f]%'
                )
        );

        CREATE INDEX [ix_audit_events_entity]
            ON [audit].[audit_events]
            ([environment], [entity_type], [entity_reference], [event_at_utc], [audit_event_id])
            INCLUDE ([event_type], [action], [actor_type], [actor_id], [correlation_id]);

        CREATE INDEX [ix_audit_events_correlation]
            ON [audit].[audit_events] ([correlation_id], [event_at_utc], [audit_event_id]);
    END;

    IF OBJECT_ID(N'[audit].[audit_event_entity_links]', N'U') IS NULL
    BEGIN
        CREATE TABLE [audit].[audit_event_entity_links]
        (
            [audit_event_entity_link_id] bigint IDENTITY(1,1) NOT NULL,
            [audit_event_id] bigint NOT NULL,
            [entity_type] varchar(100) NOT NULL,
            [entity_reference] varchar(200) NOT NULL,
            [relationship_type] varchar(40) NOT NULL,
            [created_at_utc] datetime2(7) NOT NULL
                CONSTRAINT [df_audit_event_entity_links_created_at_utc] DEFAULT SYSUTCDATETIME(),
            [created_by] nvarchar(256) NOT NULL,
            CONSTRAINT [pk_audit_event_entity_links]
                PRIMARY KEY CLUSTERED ([audit_event_entity_link_id]),
            CONSTRAINT [uq_audit_event_entity_links_entity]
                UNIQUE ([audit_event_id], [entity_type], [entity_reference], [relationship_type]),
            CONSTRAINT [fk_audit_event_entity_links_event]
                FOREIGN KEY ([audit_event_id])
                REFERENCES [audit].[audit_events] ([audit_event_id]),
            CONSTRAINT [ck_audit_event_entity_links_relationship]
                CHECK ([relationship_type] IN ('PRIMARY', 'PARENT', 'CHILD', 'AFFECTED', 'APPROVAL', 'EVIDENCE', 'RELATED'))
        );
    END;

    UPDATE [operations].[database_metadata]
    SET
        [schema_baseline_version] = 'V0009',
        [updated_at_utc] = SYSUTCDATETIME(),
        [updated_by] = COALESCE(SUSER_SNAME(), N'UNKNOWN')
    WHERE [database_metadata_id] = 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
