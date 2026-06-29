/*
Verification: V0009__verify_operational_foundation_tables.sql
Purpose:
  Verify durable transport, scheduled jobs, incidents, operational controls,
  alerts and audit storage, including trusted constraints, filtered indexes,
  rowversion projections and the V0009 baseline marker.
Expected result:
  One PASS result set and no raised verification error.
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

DECLARE @expected_tables TABLE
(
    [schema_name] sysname NOT NULL,
    [table_name] sysname NOT NULL,
    PRIMARY KEY ([schema_name], [table_name])
);

INSERT INTO @expected_tables ([schema_name], [table_name])
VALUES
    (N'operations', N'outbox_messages'),
    (N'operations', N'outbox_delivery_attempts'),
    (N'operations', N'inbox_messages'),
    (N'operations', N'inbox_processing_attempts'),
    (N'operations', N'scheduled_jobs'),
    (N'operations', N'job_runs'),
    (N'operations', N'job_run_events'),
    (N'operations', N'incidents'),
    (N'operations', N'incident_events'),
    (N'operations', N'incident_entity_links'),
    (N'operations', N'operational_controls'),
    (N'operations', N'operational_control_approvals'),
    (N'operations', N'operational_control_states'),
    (N'operations', N'operational_control_events'),
    (N'operations', N'scope_operating_states'),
    (N'operations', N'alerts'),
    (N'operations', N'alert_deliveries'),
    (N'audit', N'audit_events'),
    (N'audit', N'audit_event_entity_links');

IF EXISTS
(
    SELECT 1
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL
)
BEGIN
    SELECT expected.[schema_name], expected.[table_name] AS [missing_table]
    FROM @expected_tables AS expected
    WHERE OBJECT_ID
    (
        QUOTENAME(expected.[schema_name]) + N'.' + QUOTENAME(expected.[table_name]),
        N'U'
    ) IS NULL;

    RAISERROR('V0009 table verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_foreign_keys TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [foreign_key_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [foreign_key_name])
);

INSERT INTO @expected_foreign_keys ([table_name], [foreign_key_name])
VALUES
    (N'[operations].[outbox_delivery_attempts]', N'fk_outbox_delivery_attempts_message'),
    (N'[operations].[inbox_processing_attempts]', N'fk_inbox_processing_attempts_message'),
    (N'[operations].[job_runs]', N'fk_job_runs_job'),
    (N'[operations].[job_run_events]', N'fk_job_run_events_run'),
    (N'[operations].[incident_events]', N'fk_incident_events_incident'),
    (N'[operations].[incident_entity_links]', N'fk_incident_entity_links_incident'),
    (N'[operations].[operational_controls]', N'fk_operational_controls_incident'),
    (N'[operations].[operational_control_approvals]', N'fk_operational_control_approvals_control'),
    (N'[operations].[operational_control_states]', N'fk_operational_control_states_control'),
    (N'[operations].[operational_control_states]', N'fk_operational_control_states_superseding_control'),
    (N'[operations].[operational_control_events]', N'fk_operational_control_events_control'),
    (N'[operations].[operational_control_events]', N'fk_operational_control_events_approval'),
    (N'[operations].[scope_operating_states]', N'fk_scope_operating_states_control'),
    (N'[operations].[alerts]', N'fk_alerts_incident'),
    (N'[operations].[alert_deliveries]', N'fk_alert_deliveries_alert'),
    (N'[audit].[audit_events]', N'fk_audit_events_incident'),
    (N'[audit].[audit_events]', N'fk_audit_events_control'),
    (N'[audit].[audit_event_entity_links]', N'fk_audit_event_entity_links_event');

IF EXISTS
(
    SELECT 1
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT expected.[table_name], expected.[foreign_key_name] AS [missing_or_untrusted_foreign_key]
    FROM @expected_foreign_keys AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[foreign_key_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0009 foreign-key verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_indexes TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [index_name] sysname NOT NULL,
    [must_be_unique] bit NOT NULL,
    [must_be_filtered] bit NOT NULL,
    PRIMARY KEY ([table_name], [index_name])
);

INSERT INTO @expected_indexes
(
    [table_name], [index_name], [must_be_unique], [must_be_filtered]
)
VALUES
    (N'[operations].[outbox_messages]', N'ux_outbox_messages_idempotency', 1, 1),
    (N'[operations].[outbox_messages]', N'ix_outbox_messages_dispatch', 0, 0),
    (N'[operations].[outbox_messages]', N'ix_outbox_messages_correlation', 0, 0),
    (N'[operations].[outbox_delivery_attempts]', N'ix_outbox_delivery_attempts_latest', 0, 0),
    (N'[operations].[inbox_messages]', N'ix_inbox_messages_processing', 0, 0),
    (N'[operations].[inbox_messages]', N'ix_inbox_messages_correlation', 0, 0),
    (N'[operations].[scheduled_jobs]', N'ix_scheduled_jobs_due', 0, 0),
    (N'[operations].[job_runs]', N'ix_job_runs_dispatch', 0, 0),
    (N'[operations].[job_runs]', N'ix_job_runs_job_time', 0, 0),
    (N'[operations].[incidents]', N'ix_incidents_open', 0, 0),
    (N'[operations].[incident_events]', N'ix_incident_events_latest', 0, 0),
    (N'[operations].[operational_controls]', N'ix_operational_controls_scope', 0, 0),
    (N'[operations].[operational_control_approvals]', N'ix_operational_control_approvals_pending', 0, 0),
    (N'[operations].[operational_control_states]', N'ix_operational_control_states_active', 0, 0),
    (N'[operations].[operational_control_events]', N'ix_operational_control_events_latest', 0, 0),
    (N'[operations].[scope_operating_states]', N'ix_scope_operating_states_mode', 0, 0),
    (N'[operations].[alerts]', N'ix_alerts_open', 0, 0),
    (N'[operations].[alerts]', N'ix_alerts_dedupe', 0, 0),
    (N'[operations].[alert_deliveries]', N'ix_alert_deliveries_pending', 0, 0),
    (N'[audit].[audit_events]', N'ix_audit_events_entity', 0, 0),
    (N'[audit].[audit_events]', N'ix_audit_events_correlation', 0, 0);

IF EXISTS
(
    SELECT 1
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    )
)
BEGIN
    SELECT expected.[table_name], expected.[index_name] AS [missing_or_invalid_index]
    FROM @expected_indexes AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS actual
        WHERE actual.[object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[index_name]
          AND actual.[is_unique] = expected.[must_be_unique]
          AND actual.[has_filter] = expected.[must_be_filtered]
          AND actual.[is_disabled] = 0
    );

    RAISERROR('V0009 index verification failed.', 16, 1);
    RETURN;
END;

DECLARE @expected_checks TABLE
(
    [table_name] nvarchar(300) NOT NULL,
    [constraint_name] sysname NOT NULL,
    PRIMARY KEY ([table_name], [constraint_name])
);

INSERT INTO @expected_checks ([table_name], [constraint_name])
VALUES
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_environment'),
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_payload_json'),
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_status'),
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_attempts'),
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_lease'),
    (N'[operations].[outbox_messages]', N'ck_outbox_messages_terminal'),
    (N'[operations].[outbox_delivery_attempts]', N'ck_outbox_delivery_attempts_outcome'),
    (N'[operations].[outbox_delivery_attempts]', N'ck_outbox_delivery_attempts_completion'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_environment'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_payload_json'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_status'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_attempts'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_lease'),
    (N'[operations].[inbox_messages]', N'ck_inbox_messages_terminal'),
    (N'[operations].[inbox_processing_attempts]', N'ck_inbox_processing_attempts_outcome'),
    (N'[operations].[scheduled_jobs]', N'ck_scheduled_jobs_schedule_type'),
    (N'[operations].[scheduled_jobs]', N'ck_scheduled_jobs_schedule_expression'),
    (N'[operations].[scheduled_jobs]', N'ck_scheduled_jobs_limits'),
    (N'[operations].[job_runs]', N'ck_job_runs_status'),
    (N'[operations].[job_runs]', N'ck_job_runs_attempts'),
    (N'[operations].[job_runs]', N'ck_job_runs_lease'),
    (N'[operations].[job_runs]', N'ck_job_runs_completion'),
    (N'[operations].[job_run_events]', N'ck_job_run_events_type'),
    (N'[operations].[incidents]', N'ck_incidents_severity'),
    (N'[operations].[incidents]', N'ck_incidents_scope'),
    (N'[operations].[incidents]', N'ck_incidents_status'),
    (N'[operations].[incidents]', N'ck_incidents_lifecycle'),
    (N'[operations].[incident_events]', N'ck_incident_events_type'),
    (N'[operations].[incident_events]', N'ck_incident_events_actor'),
    (N'[operations].[operational_controls]', N'ck_operational_controls_contract_version'),
    (N'[operations].[operational_controls]', N'ck_operational_controls_type'),
    (N'[operations].[operational_controls]', N'ck_operational_controls_scope'),
    (N'[operations].[operational_controls]', N'ck_operational_controls_mode'),
    (N'[operations].[operational_controls]', N'ck_operational_controls_raw_json'),
    (N'[operations].[operational_control_approvals]', N'ck_operational_control_approvals_decision'),
    (N'[operations].[operational_control_approvals]', N'ck_operational_control_approvals_independent'),
    (N'[operations].[operational_control_states]', N'ck_operational_control_states_reset'),
    (N'[operations].[operational_control_states]', N'ck_operational_control_states_superseded'),
    (N'[operations].[operational_control_events]', N'ck_operational_control_events_type'),
    (N'[operations].[operational_control_events]', N'ck_operational_control_events_approval'),
    (N'[operations].[scope_operating_states]', N'ck_scope_operating_states_mode'),
    (N'[operations].[scope_operating_states]', N'ck_scope_operating_states_exit_safety'),
    (N'[operations].[scope_operating_states]', N'ck_scope_operating_states_control'),
    (N'[operations].[alerts]', N'ck_alerts_severity'),
    (N'[operations].[alerts]', N'ck_alerts_status'),
    (N'[operations].[alerts]', N'ck_alerts_lifecycle'),
    (N'[operations].[alert_deliveries]', N'ck_alert_deliveries_status'),
    (N'[operations].[alert_deliveries]', N'ck_alert_deliveries_completion'),
    (N'[audit].[audit_events]', N'ck_audit_events_actor'),
    (N'[audit].[audit_events]', N'ck_audit_events_values'),
    (N'[audit].[audit_events]', N'ck_audit_events_before_json'),
    (N'[audit].[audit_events]', N'ck_audit_events_after_json'),
    (N'[audit].[audit_events]', N'ck_audit_events_hash'),
    (N'[audit].[audit_event_entity_links]', N'ck_audit_event_entity_links_relationship');

IF EXISTS
(
    SELECT 1
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    )
)
BEGIN
    SELECT expected.[table_name], expected.[constraint_name] AS [missing_or_untrusted_check]
    FROM @expected_checks AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints AS actual
        WHERE actual.[parent_object_id] = OBJECT_ID(expected.[table_name])
          AND actual.[name] = expected.[constraint_name]
          AND actual.[is_disabled] = 0
          AND actual.[is_not_trusted] = 0
    );

    RAISERROR('V0009 check-constraint verification failed.', 16, 1);
    RETURN;
END;

DECLARE @rowversion_tables TABLE
(
    [table_name] nvarchar(300) NOT NULL PRIMARY KEY
);

INSERT INTO @rowversion_tables ([table_name])
VALUES
    (N'[operations].[outbox_messages]'),
    (N'[operations].[inbox_messages]'),
    (N'[operations].[scheduled_jobs]'),
    (N'[operations].[job_runs]'),
    (N'[operations].[incidents]'),
    (N'[operations].[operational_control_states]'),
    (N'[operations].[scope_operating_states]'),
    (N'[operations].[alerts]');

IF EXISTS
(
    SELECT 1
    FROM @rowversion_tables AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns AS columns
        INNER JOIN sys.types AS types
            ON columns.[user_type_id] = types.[user_type_id]
        WHERE columns.[object_id] = OBJECT_ID(expected.[table_name])
          AND columns.[name] = N'row_version'
          AND types.[name] = N'timestamp'
    )
)
BEGIN
    SELECT expected.[table_name] AS [missing_rowversion_projection]
    FROM @rowversion_tables AS expected
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM sys.columns AS columns
        INNER JOIN sys.types AS types
            ON columns.[user_type_id] = types.[user_type_id]
        WHERE columns.[object_id] = OBJECT_ID(expected.[table_name])
          AND columns.[name] = N'row_version'
          AND types.[name] = N'timestamp'
    );

    RAISERROR('V0009 rowversion projection verification failed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [operations].[database_metadata]
    WHERE [database_metadata_id] = 1
      AND [schema_baseline_version] = 'V0009'
)
BEGIN
    RAISERROR('Database metadata was not advanced to V0009.', 16, 1);
    RETURN;
END;

SELECT
    'PASS' AS [verification_status],
    'V0009' AS [migration_version],
    DB_NAME() AS [database_name],
    (SELECT COUNT_BIG(*) FROM @expected_tables) AS [verified_table_count],
    (SELECT COUNT_BIG(*) FROM @expected_foreign_keys) AS [verified_foreign_key_count],
    (SELECT COUNT_BIG(*) FROM @expected_indexes) AS [verified_index_count],
    (SELECT COUNT_BIG(*) FROM @expected_checks) AS [verified_check_count],
    (SELECT COUNT_BIG(*) FROM @rowversion_tables) AS [verified_projection_count];
